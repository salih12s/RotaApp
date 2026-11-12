using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Infrastructure;

namespace Rota.Notifications.Api.Features.Consumers;

/// <summary>
/// <c>OrderStatusChanged.v1</c> → one <c>webhook_deliveries</c> row for the order's merchant (docs/05 §3, §4). Runs inside the consumer's
/// transaction with the inbox row, so a redelivered event creates nothing twice; the unique (merchant, event) index is the second guard.
/// A status merchants are not told about (<c>Created</c>, <c>Dispatching</c>) and a merchant without an active subscription produce no row.
/// </summary>
public sealed class OrderStatusChangedHandler(NotificationsDbContext db, TimeProvider time) : IMessageHandler<OrderStatusChangedV1>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(EventEnvelope<OrderStatusChangedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var change = message.Data;
        if (WebhookEventTypes.TypeFor(change.ToStatus) is not { } type)
        {
            return;
        }

        if (!await db.Subscriptions.AnyAsync(s => s.MerchantId == change.MerchantId && s.IsActive, cancellationToken))
        {
            return;
        }

        // The name is a nicety copied from Dispatch's events; a courier we have not heard of yet is sent without one.
        string? courierName = null;
        if (change.CourierId is { } courierId)
        {
            courierName = (await db.CourierNames.AsNoTracking().SingleOrDefaultAsync(c => c.CourierId == courierId, cancellationToken))?.DisplayName;
        }

        // docs/05 §4: "id" is the event id, so the merchant can drop a repeat; "orderVersion" lets it drop an out-of-order old one.
        var body = new
        {
            id = message.EventId,
            type,
            createdAt = change.OccurredAt,
            data = new
            {
                orderId = change.OrderId,
                merchantOrderRef = change.MerchantOrderRef,
                status = change.ToStatus,
                orderVersion = change.OrderVersion,
                courier = change.CourierId is null ? null : new { id = change.CourierId, displayName = courierName },
                reason = change.Reason,
            },
        };

        db.Deliveries.Add(WebhookDelivery.Create(change.MerchantId, message.EventId, type, JsonSerializer.Serialize(body, _json), time.GetUtcNow()));
    }
}

/// <summary><c>CourierStateChanged.v1</c> → keeps a courier's display name (only the name, only the newest version).</summary>
public sealed class CourierStateChangedHandler(NotificationsDbContext db) : IMessageHandler<CourierStateChangedV1>
{
    public async Task HandleAsync(EventEnvelope<CourierStateChangedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var courier = message.Data;

        // An older event arriving late must not overwrite a newer name: the conditional DO UPDATE only moves forward.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO courier_names (courier_id, display_name, version)
            VALUES ({courier.CourierId}, {courier.DisplayName}, {courier.CourierVersion})
            ON CONFLICT (courier_id) DO UPDATE
            SET display_name = EXCLUDED.display_name, version = EXCLUDED.version
            WHERE courier_names.version < EXCLUDED.version
            """, cancellationToken);
    }
}
