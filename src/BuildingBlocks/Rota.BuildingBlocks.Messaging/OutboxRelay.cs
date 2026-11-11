using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// Publishes outbox rows to RabbitMQ, then marks them processed. The only place events enter the broker.
/// </summary>
/// <remarks>
/// Guarantee: <b>at least once</b>. A row is marked processed only after the broker confirmed it; if the
/// process dies between confirm and commit, the row is published again and the consumer's inbox drops
/// the duplicate. Order is preserved per relay instance; with several instances, consumers use
/// <c>aggregateVersion</c> to ignore stale events.
/// </remarks>
public sealed class OutboxRelay<TContext>(
    IServiceScopeFactory scopes,
    RabbitMqConnection broker,
    TimeProvider time,
    IOptions<MessagingOptions> options,
    ILogger<OutboxRelay<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            MessagingLog.Disabled(logger, "outbox relay");
            return;
        }

        var nextCleanup = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            try
            {
                var round = await RelayBatchAsync(stoppingToken);

                if (time.GetUtcNow() >= nextCleanup)
                {
                    await CleanUpAsync(stoppingToken);
                    nextCleanup = time.GetUtcNow() + settings.CleanupInterval;
                }

                switch (round)
                {
                    case RelayRound.Empty:
                        await Task.Delay(settings.RelayPollInterval, stoppingToken);
                        break;
                    case RelayRound.Failed:
                        await Task.Delay(settings.ReconnectDelay, stoppingToken);
                        break;
                    case RelayRound.Published:
                    default:
                        break; // more may be waiting: go again immediately
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database or broker down: the outbox is the buffer, so just wait and try again.
                MessagingLog.RelayFailed(logger, ex);
                await DiscardChannelAsync();
                await DelayQuietlyAsync(settings.ReconnectDelay, stoppingToken);
            }
        }

        await DiscardChannelAsync();
    }

    private async Task<RelayRound> RelayBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var batchSize = options.Value.RelayBatchSize;

        // The row locks live as long as this transaction. SKIP LOCKED makes a second relay instance
        // take other rows instead of waiting, so instances share the work and never double-publish a batch.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Set<OutboxMessage>()
            .FromSql($"SELECT * FROM outbox_messages WHERE processed_at IS NULL ORDER BY occurred_at, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return RelayRound.Empty;
        }

        var channel = await GetChannelAsync(cancellationToken);
        var failed = false;
        var published = 0;
        foreach (var row in rows)
        {
            try
            {
                await PublishAsync(channel, row, cancellationToken);
                row.ProcessedAt = time.GetUtcNow();
                row.LastError = null;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stop at the first failure: later rows must not overtake this one.
                row.AttemptCount++;
                row.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}", 1000);
                MessagingLog.PublishFailed(logger, ex, row.Id, row.RoutingKey, row.AttemptCount);
                MessagingTelemetry.PublishFailed.Add(1);
                failed = true;
                break;
            }
        }

        // One commit for the whole batch: the successes and the failure's error note together.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        MessagingTelemetry.Published.Add(published);

        if (failed)
        {
            await DiscardChannelAsync(); // a nacked or timed-out channel is not trusted again
            return RelayRound.Failed;
        }

        return RelayRound.Published;
    }

    private static async Task PublishAsync(IChannel channel, OutboxMessage row, CancellationToken cancellationToken)
    {
        // Continue the trace of the request that wrote the row, so POST → relay → consumer is one trace.
        ActivityContext parent = default;
        if (row.TraceParent is not null)
        {
            ActivityContext.TryParse(row.TraceParent, null, out parent);
        }

        using var activity = MessagingTelemetry.Source.StartActivity($"{row.RoutingKey} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Topology.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", row.RoutingKey);
        activity?.SetTag("messaging.message.id", row.Id.ToString("D"));

        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent, // survives a broker restart
            MessageId = row.Id.ToString("D"),
            Type = row.Type,
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        if ((activity?.Id ?? row.TraceParent) is { } traceParent)
        {
            properties.Headers[MessageHeaders.TraceParent] = traceParent;
        }

        // Completes only after the broker's publisher confirm; throws if it nacks.
        await channel.BasicPublishAsync(
            Topology.EventsExchange, row.RoutingKey, mandatory: false, properties, Encoding.UTF8.GetBytes(row.Payload), cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await DiscardChannelAsync();
        var channel = await broker.CreateChannelAsync(publisherConfirms: true, cancellationToken);
        await Topology.DeclareEventsAsync(channel, cancellationToken);
        _channel = channel;
        return channel;
    }

    private async Task DiscardChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception)
            {
                // Already broken; nothing left to release.
            }
        }
    }

    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var cutoff = time.GetUtcNow() - options.Value.OutboxRetention;

        var removed = await db.DeleteProcessedOutboxOlderThanAsync(cutoff, cancellationToken);
        if (removed > 0)
        {
            MessagingLog.Cleaned(logger, removed, cutoff);
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the loop condition ends the service.
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private enum RelayRound
    {
        Empty,
        Published,
        Failed,
    }
}

/// <summary>Header names on messages (docs/05-contracts.md §3 plus the retry bookkeeping).</summary>
public static class MessageHeaders
{
    public const string TraceParent = "traceparent";

    /// <summary>1 for the first delivery; incremented each time the consumer sends the message to a retry queue.</summary>
    public const string Attempt = "x-attempt";

    public const string Error = "x-error";

    public const string SourceQueue = "x-source-queue";
}
