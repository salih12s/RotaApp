using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.GraphQL;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.Features.Consumers;

/// <summary>
/// An order event whose order is not in the read model yet. Events travel on separate queues, so a status change can overtake the
/// <c>OrderCreated</c> it follows. Failing makes the consumer retry later with a delay (docs/05 §3) instead of dropping the change.
/// </summary>
public sealed class OrderViewNotReadyException(Guid orderId)
    : InvalidOperationException($"Order {orderId} is not in the read model yet; its OrderCreated has not been applied.");

/// <summary><c>OrderCreated.v1</c> → a new <c>order_view</c> row. A repeat (or a row a newer event already moved on) changes nothing.</summary>
public sealed class OrderCreatedHandler(OpsDbContext db) : IMessageHandler<OrderCreatedV1>
{
    public async Task HandleAsync(EventEnvelope<OrderCreatedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var order = message.Data;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO order_view (order_id, merchant_id, merchant_order_ref, status, courier_id,
                pickup_lat, pickup_lon, dropoff_lat, dropoff_lon, created_at, order_version)
            VALUES ({order.OrderId}, {order.MerchantId}, {order.MerchantOrderRef}, 'Created', NULL,
                {order.Pickup.Lat}, {order.Pickup.Lon}, {order.Dropoff.Lat}, {order.Dropoff.Lon}, {order.CreatedAt}, {order.OrderVersion})
            ON CONFLICT (order_id) DO NOTHING
            """, cancellationToken);
    }
}

/// <summary>
/// <c>OrderStatusChanged.v1</c> → moves the row forward, only if the event is newer than what the row already holds
/// (<c>order_version &lt; @v</c>, docs/04). An old event arriving late updates 0 rows and is ignored.
/// </summary>
public sealed class OrderStatusChangedHandler(OpsDbContext db, OrderStatusNotifier? notifier = null) : IMessageHandler<OrderStatusChangedV1>
{
    public async Task HandleAsync(EventEnvelope<OrderStatusChangedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var change = message.Data;
        var to = change.ToStatus;

        // The milestone timestamps are set by the transition into that status and never cleared (a later transition keeps them).
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE order_view
            SET status = {to},
                courier_id = {change.CourierId},
                assigned_at = CASE WHEN {to} = 'Assigned' THEN {change.OccurredAt} ELSE assigned_at END,
                picked_up_at = CASE WHEN {to} = 'PickedUp' THEN {change.OccurredAt} ELSE picked_up_at END,
                delivered_at = CASE WHEN {to} = 'Delivered' THEN {change.OccurredAt} ELSE delivered_at END,
                order_version = {change.OrderVersion}
            WHERE order_id = {change.OrderId} AND order_version < {change.OrderVersion}
            """, cancellationToken);

        if (updated == 0 && !await db.Orders.AnyAsync(o => o.OrderId == change.OrderId, cancellationToken))
        {
            throw new OrderViewNotReadyException(change.OrderId);
        }

        // Only a change that was applied is news: an old or repeated event moved nothing, so the live panel hears nothing.
        if (updated > 0 && notifier is not null)
        {
            await notifier.PublishAsync(change, cancellationToken);
        }
    }
}

/// <summary><c>CourierStateChanged.v1</c> → upserts <c>courier_view</c>, moving forward only (<c>courier_version</c>).</summary>
public sealed class CourierStateChangedHandler(OpsDbContext db) : IMessageHandler<CourierStateChangedV1>
{
    public async Task HandleAsync(EventEnvelope<CourierStateChangedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var courier = message.Data;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO courier_view (courier_id, display_name, state, current_order, updated_at, courier_version)
            VALUES ({courier.CourierId}, {courier.DisplayName}, {courier.State}, {courier.CurrentOrderId}, {courier.OccurredAt}, {courier.CourierVersion})
            ON CONFLICT (courier_id) DO UPDATE
            SET display_name = EXCLUDED.display_name, state = EXCLUDED.state, current_order = EXCLUDED.current_order,
                updated_at = EXCLUDED.updated_at, courier_version = EXCLUDED.courier_version
            WHERE courier_view.courier_version < EXCLUDED.courier_version
            """, cancellationToken);
    }
}
