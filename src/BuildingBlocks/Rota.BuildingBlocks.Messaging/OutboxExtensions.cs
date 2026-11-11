using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rota.Contracts.Events;

namespace Rota.BuildingBlocks.Messaging;

public static class OutboxExtensions
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds the event to the outbox of the current unit of work. Nothing is sent here: the row is
    /// committed by the caller's <c>SaveChanges</c> together with the business data, and the relay (F03)
    /// publishes it afterwards.
    /// </summary>
    public static OutboxMessage AddToOutbox<TEvent>(
        this DbContext db,
        TEvent data,
        Guid aggregateId,
        int aggregateVersion,
        DateTimeOffset occurredAt)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(data);

        // Version 7: time-ordered ids keep the primary key index append-only.
        var eventId = Guid.CreateVersion7(occurredAt);
        var envelope = new EventEnvelope<TEvent>(eventId, TEvent.EventType, occurredAt, aggregateId, aggregateVersion, data);

        var message = new OutboxMessage
        {
            Id = eventId,
            Type = TEvent.EventType,
            RoutingKey = TEvent.RoutingKey,
            Payload = JsonSerializer.Serialize(envelope, _json),
            TraceParent = Activity.Current?.Id,
            OccurredAt = occurredAt,
        };

        db.Set<OutboxMessage>().Add(message);
        return message;
    }

    /// <summary>
    /// Deletes published rows older than the cutoff (docs/04: 7 days). Unprocessed rows are never
    /// touched, however old: an unpublished event is a promise still owed.
    /// </summary>
    public static Task<int> DeleteProcessedOutboxOlderThanAsync(this DbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
