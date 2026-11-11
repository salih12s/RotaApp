using RabbitMQ.Client;

namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// The RabbitMQ layout, defined in code (docs/05-contracts.md §3). Declaring is idempotent, so every
/// service declares what it needs at startup; the broker needs no hand-made setup.
/// </summary>
/// <remarks>
/// <code>
/// rota.events (topic) ──routing key──► q.&lt;service&gt;.&lt;consumer&gt; ──fails──► (consumer republishes)
///        │ unroutable                        ▲                                      │
///        ▼ (alternate exchange)              │ TTL expires (dead-letter)            ▼
/// rota.unrouted ──► q.rota.unrouted    q.….retry.N  ◄──────────────────────────────┘
///                                                     after the last retry ──► q.….dlq
/// </code>
/// </remarks>
public static class Topology
{
    public const string EventsExchange = "rota.events";

    /// <summary>
    /// Events that match no queue are parked here instead of vanishing. Needed because queues are
    /// declared by their consumers: before a consumer's first start there is nothing to route to.
    /// </summary>
    public const string UnroutedExchange = "rota.unrouted";

    public const string UnroutedQueue = "q.rota.unrouted";

    public static string RetryQueue(string queue, int retryNumber) => $"{queue}.retry.{retryNumber}";

    public static string DeadLetterQueue(string queue) => $"{queue}.dlq";

    public static async Task DeclareEventsAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Fanout exchange first: the events exchange refers to it as its alternate exchange.
        await channel.ExchangeDeclareAsync(UnroutedExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(UnroutedQueue, durable: true, exclusive: false, autoDelete: false, QuorumArguments(
            new Dictionary<string, object?>
            {
                // A parking place, not an archive: bounded so events nobody ever consumes cannot fill the disk.
                ["x-message-ttl"] = (long)TimeSpan.FromDays(7).TotalMilliseconds,
                ["x-max-length"] = 100_000,
                ["x-overflow"] = "drop-head",
            }), cancellationToken: cancellationToken);
        await channel.QueueBindAsync(UnroutedQueue, UnroutedExchange, string.Empty, cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["alternate-exchange"] = UnroutedExchange },
            cancellationToken: cancellationToken);
    }

    /// <summary>The consumer's queue, its retry queues (one per delay) and its dead-letter queue.</summary>
    public static async Task DeclareConsumerAsync(
        IChannel channel, string queue, string routingKey, IReadOnlyList<TimeSpan> retryDelays, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(retryDelays);

        await DeclareEventsAsync(channel, cancellationToken);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, QuorumArguments(), cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue, EventsExchange, routingKey, cancellationToken: cancellationToken);

        for (var i = 0; i < retryDelays.Count; i++)
        {
            // Queue-level TTL: every message waits the same time, so a short wait is never stuck behind
            // a long one (per-message TTL only expires at the head of the queue).
            await channel.QueueDeclareAsync(RetryQueue(queue, i + 1), durable: true, exclusive: false, autoDelete: false, QuorumArguments(
                new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = (long)retryDelays[i].TotalMilliseconds,
                    // Default exchange + queue name as key = straight back into the consumer's queue.
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = queue,
                }), cancellationToken: cancellationToken);
        }

        await channel.QueueDeclareAsync(DeadLetterQueue(queue), durable: true, exclusive: false, autoDelete: false, QuorumArguments(), cancellationToken: cancellationToken);
    }

    // Quorum queues replicate with Raft and keep messages across broker restarts and node loss.
    private static Dictionary<string, object?> QuorumArguments(Dictionary<string, object?>? extra = null)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-queue-type"] = "quorum" };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                arguments[key] = value;
            }
        }

        return arguments;
    }
}
