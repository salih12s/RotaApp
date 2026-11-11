using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>
/// The test's own plain RabbitMQ client: injects raw messages (to simulate redelivery or bad data) and
/// looks into queues. Test-only; product code publishes events solely through the outbox relay.
/// </summary>
public sealed class BrokerProbe : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private BrokerProbe(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<BrokerProbe> ConnectAsync(string uri, CancellationToken cancellationToken)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        // Same idempotent declaration the services use: the probe can publish before any service started.
        await Topology.DeclareEventsAsync(channel, cancellationToken);
        return new BrokerProbe(connection, channel);
    }

    /// <summary>Publishes an event exactly as the relay would. Use the same <paramref name="eventId"/> twice to simulate redelivery.</summary>
    public Task PublishEventAsync<TEvent>(TEvent data, Guid eventId, Guid aggregateId, int aggregateVersion, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        var envelope = new EventEnvelope<TEvent>(eventId, TEvent.EventType, TestClock.Start, aggregateId, aggregateVersion, data);
        return PublishRawAsync(TEvent.RoutingKey, TEvent.EventType, eventId, JsonSerializer.Serialize(envelope, _json), cancellationToken);
    }

    public Task PublishRawAsync(string routingKey, string type, Guid messageId, string body, CancellationToken cancellationToken) =>
        _channel.BasicPublishAsync(
            Topology.EventsExchange,
            routingKey,
            mandatory: false,
            new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = messageId.ToString("D"),
                Type = type,
                ContentType = "application/json",
            },
            Encoding.UTF8.GetBytes(body),
            cancellationToken).AsTask();

    /// <summary>Messages waiting in the queue (not counting ones a consumer holds unacknowledged); null if the queue does not exist.</summary>
    public async Task<uint?> MessageCountAsync(string queue, CancellationToken cancellationToken)
    {
        // A passive declare of a missing queue closes the channel, so use a throwaway one.
        await using var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue, cancellationToken)).MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return null;
        }
    }

    /// <summary>Takes one message off the queue (for inspecting a DLQ entry).</summary>
    public async Task<ReceivedMessage?> TakeAsync(string queue, CancellationToken cancellationToken)
    {
        var result = await _channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
        if (result is null)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in result.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            headers[key] = value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                null => string.Empty,
                _ => value.ToString() ?? string.Empty,
            };
        }

        return new ReceivedMessage(result.BasicProperties.MessageId, Encoding.UTF8.GetString(result.Body.Span), headers);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed record ReceivedMessage(string? MessageId, string Body, IReadOnlyDictionary<string, string> Headers);
