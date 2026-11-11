using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Rota.BuildingBlocks.Persistence;
using Rota.Contracts.Events;

namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// Business code for one event type. It runs inside the consumer's database transaction, together
/// with the inbox row: when it throws, nothing it wrote survives and the message is retried.
/// Return normally for "done" and for "deliberately ignored" (e.g. the order was already cancelled).
/// </summary>
public interface IMessageHandler<TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(EventEnvelope<TEvent> message, CancellationToken cancellationToken);
}

/// <summary>Queue and consumer name of one subscription, e.g. <c>q.dispatch.order-created</c> / <c>Dispatch.OrderCreatedConsumer</c>.</summary>
public sealed record ConsumerDefinition(string Queue, string Name);

/// <summary>
/// Consumes one queue with the inbox pattern (docs/04 "Ortak: outbox ve inbox").
/// </summary>
/// <remarks>
/// Per message, in this order:
/// <list type="number">
/// <item>open a transaction; insert (eventId, consumer) into <c>inbox_messages</c> — a primary-key clash means "already processed";</item>
/// <item>run the handler; save; <b>commit</b>;</item>
/// <item>only then <b>ACK</b>. A crash before the ACK redelivers the message, and the inbox row (committed with the work) turns the redelivery into a no-op.</item>
/// </list>
/// A failure sends the message to the next retry queue (a delayed return) and ACKs the original, so the queue
/// is never blocked by one bad message; after the last retry it is parked in the DLQ. Manual ACK only.
/// </remarks>
public sealed class ConsumerService<TContext, TEvent, THandler>(
    ConsumerDefinition definition,
    IServiceScopeFactory scopes,
    RabbitMqConnection broker,
    TimeProvider time,
    IOptions<MessagingOptions> options,
    ILogger<ConsumerService<TContext, TEvent, THandler>> logger) : BackgroundService
    where TContext : DbContext
    where TEvent : IIntegrationEvent
    where THandler : class, IMessageHandler<TEvent>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // How long a dead channel may stay closed before we assume automatic recovery gave up.
    private static readonly TimeSpan _closedGrace = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            MessagingLog.Disabled(logger, definition.Name);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilBrokenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MessagingLog.ConsumerReconnecting(logger, ex, definition.Name);
                try
                {
                    await Task.Delay(options.Value.ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConsumeUntilBrokenAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        await using var consumeChannel = await broker.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        await using var republishChannel = await broker.CreateChannelAsync(publisherConfirms: true, stoppingToken);

        await Topology.DeclareConsumerAsync(consumeChannel, definition.Queue, TEvent.RoutingKey, settings.EffectiveRetryDelays, stoppingToken);
        await consumeChannel.BasicQosAsync(prefetchSize: 0, settings.ConsumerPrefetch, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(consumeChannel);
        consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(consumeChannel, republishChannel, delivery, stoppingToken);

        // autoAck: false — a message leaves the queue only when we ACK it, after the commit.
        await consumeChannel.BasicConsumeAsync(definition.Queue, autoAck: false, consumer, stoppingToken);

        // The client recovers channels after a broker restart by itself; we only step in if it stays dead.
        var closedSince = (long?)null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (consumeChannel.IsOpen && republishChannel.IsOpen)
            {
                closedSince = null;
                continue;
            }

            // A monotonic stopwatch, not a wall clock: this measures an interval, and clock changes must not matter.
            closedSince ??= Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(closedSince.Value) > _closedGrace)
            {
                throw new InvalidOperationException("The broker channel stayed closed.");
            }
        }
    }

    private async Task OnDeliveryAsync(IChannel consumeChannel, IChannel republishChannel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var messageId = delivery.BasicProperties.MessageId ?? string.Empty;
        var attempt = ReadAttempt(delivery.BasicProperties);

        ActivityContext parent = default;
        if (ReadHeader(delivery.BasicProperties, MessageHeaders.TraceParent) is { } traceParent)
        {
            ActivityContext.TryParse(traceParent, null, out parent);
        }

        using var activity = MessagingTelemetry.Source.StartActivity($"{definition.Queue} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", definition.Queue);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("messaging.rabbitmq.attempt", attempt);

        try
        {
            if (!TryReadEnvelope(delivery, out var envelope, out var problem))
            {
                // Retrying cannot fix a malformed message: park it straight away.
                await MoveAsync(republishChannel, delivery, Topology.DeadLetterQueue(definition.Queue), attempt, problem, stoppingToken);
                MessagingTelemetry.DeadLettered.Add(1);
                await consumeChannel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
                return;
            }

            if (await ProcessAsync(envelope, stoppingToken))
            {
                MessagingTelemetry.Consumed.Add(1);
            }
            else
            {
                MessagingTelemetry.Duplicates.Add(1);
                MessagingLog.Duplicate(logger, definition.Name, envelope.EventId);
            }

            // Everything above is committed. Only now is it safe to let the broker forget the message.
            await consumeChannel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-message: no ACK, so the broker redelivers it to the next instance.
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await RetryOrDeadLetterAsync(consumeChannel, republishChannel, delivery, attempt, ex, stoppingToken);
        }
    }

    /// <returns><c>true</c> if handled now, <c>false</c> if the inbox says it was handled before.</returns>
    private async Task<bool> ProcessAsync(EventEnvelope<TEvent> envelope, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = envelope.EventId,
            Consumer = definition.Name,
            ProcessedAt = time.GetUtcNow(),
        });

        try
        {
            // The primary key (message_id, consumer) is the guard. Two copies arriving together: the
            // second INSERT waits for the first transaction, then fails here once the first commits.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return false; // the transaction rolls back on dispose; nothing to undo
        }

        await handler.HandleAsync(envelope, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool TryReadEnvelope(BasicDeliverEventArgs delivery, out EventEnvelope<TEvent> envelope, out string problem)
    {
        envelope = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<EventEnvelope<TEvent>>(delivery.Body.Span, _json);
            if (parsed is null || parsed.Data is null)
            {
                problem = "Envelope or data is empty.";
                return false;
            }

            if (!string.Equals(parsed.Type, TEvent.EventType, StringComparison.Ordinal))
            {
                problem = $"Expected '{TEvent.EventType}' but got '{parsed.Type}'.";
                return false;
            }

            envelope = parsed;
            problem = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            problem = $"Malformed JSON: {ex.Message}";
            return false;
        }
    }

    private async Task RetryOrDeadLetterAsync(
        IChannel consumeChannel, IChannel republishChannel, BasicDeliverEventArgs delivery, int attempt, Exception error, CancellationToken cancellationToken)
    {
        var delays = options.Value.EffectiveRetryDelays;
        var retrying = attempt <= delays.Count;
        var target = retrying ? Topology.RetryQueue(definition.Queue, attempt) : Topology.DeadLetterQueue(definition.Queue);
        var messageId = delivery.BasicProperties.MessageId ?? string.Empty;
        MessagingLog.HandlerFailed(logger, error, definition.Name, messageId, attempt, target);

        try
        {
            // Copy first (broker-confirmed), ACK second: a crash in between yields a duplicate, never a loss.
            await MoveAsync(republishChannel, delivery, target, retrying ? attempt + 1 : attempt, $"{error.GetType().Name}: {error.Message}", cancellationToken);
            (retrying ? MessagingTelemetry.Retried : MessagingTelemetry.DeadLettered).Add(1);
            await consumeChannel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (Exception moveError) when (moveError is not OperationCanceledException)
        {
            // Could not park it (broker trouble): put it back so it is not lost.
            MessagingLog.MoveFailed(logger, moveError, definition.Name, messageId, target);
            try
            {
                await consumeChannel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
            }
            catch (Exception)
            {
                // Channel is gone; the broker redelivers an unacknowledged message on its own.
            }
        }
    }

    // Moves a message to a retry queue or the DLQ via the default exchange (routing key = queue name).
    // This is queue plumbing for an already-published message, not event publishing: events enter the
    // broker only through OutboxRelay.
    private async Task MoveAsync(
        IChannel republishChannel, BasicDeliverEventArgs delivery, string targetQueue, int attempt, string error, CancellationToken cancellationToken)
    {
        var source = delivery.BasicProperties;
        var headers = source.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(source.Headers, StringComparer.Ordinal);
        headers[MessageHeaders.Attempt] = attempt;
        headers[MessageHeaders.Error] = error.Length <= 500 ? error : error[..500];
        headers[MessageHeaders.SourceQueue] = definition.Queue;

        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = source.MessageId,
            Type = source.Type,
            ContentType = source.ContentType,
            Headers = headers,
        };

        // mandatory: the queue must exist; a silently dropped retry would be a lost message.
        await republishChannel.BasicPublishAsync(string.Empty, targetQueue, mandatory: true, properties, delivery.Body, cancellationToken);
    }

    private static int ReadAttempt(IReadOnlyBasicProperties properties) =>
        properties.Headers is not null && properties.Headers.TryGetValue(MessageHeaders.Attempt, out var value)
            ? value switch { int i => i, long l => (int)l, _ => 1 }
            : 1;

    // The client hands string headers back as bytes.
    private static string? ReadHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is not null && properties.Headers.TryGetValue(name, out var value)
            ? value switch { byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes), string text => text, _ => null }
            : null;
}
