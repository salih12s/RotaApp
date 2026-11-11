namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// An event waiting to be published. Written in the same transaction as the business change,
/// so "saved but never published" and "published but rolled back" cannot happen.
/// </summary>
public sealed class OutboxMessage
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public required string RoutingKey { get; init; }

    /// <summary>The serialized <see cref="EventEnvelope{TData}"/> (jsonb).</summary>
    public required string Payload { get; init; }

    /// <summary>W3C <c>traceparent</c> of the request that caused the event; the relay continues the trace.</summary>
    public string? TraceParent { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

/// <summary>A consumed message, keyed by (message, consumer): a redelivery hits the primary key.</summary>
public sealed class InboxMessage
{
    public required Guid MessageId { get; init; }

    public required string Consumer { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}
