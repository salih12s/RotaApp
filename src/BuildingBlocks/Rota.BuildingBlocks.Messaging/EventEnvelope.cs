namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// The wrapper every event travels in (docs/05-contracts.md §3). <see cref="EventId"/> is what
/// consumers deduplicate on (inbox); <see cref="AggregateVersion"/> lets them drop stale events.
/// </summary>
public sealed record EventEnvelope<TData>(
    Guid EventId,
    string Type,
    DateTimeOffset OccurredAt,
    Guid AggregateId,
    int AggregateVersion,
    TData Data);
