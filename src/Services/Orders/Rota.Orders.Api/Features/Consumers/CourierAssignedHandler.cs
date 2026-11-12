using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.Consumers;

/// <summary>
/// <c>CourierAssigned.v1</c> → <c>Dispatching/Unassigned → Assigned</c>, the courier id on the order, and
/// <c>OrderStatusChanged.v1</c> in the outbox. An order that moved on meanwhile (e.g. the merchant cancelled it) refuses the
/// transition: the event is acknowledged and counted, not retried. Dispatch frees the courier when it sees the cancellation.
/// </summary>
public sealed partial class CourierAssignedHandler(OrdersDbContext db, TimeProvider time, ILogger<CourierAssignedHandler> logger)
    : IMessageHandler<CourierAssignedV1>
{
    public async Task HandleAsync(EventEnvelope<CourierAssignedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var assigned = message.Data;

        // Not filtered by merchant: a system consumer, not a caller acting for someone.
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == assigned.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {assigned.OrderId} does not exist, but Dispatch assigned a courier to it.");

        var now = time.GetUtcNow();
        var from = order.Status;
        var moved = order.Assign(assigned.CourierId, OrderActor.System, now);
        if (moved.IsFailure)
        {
            MessagingTelemetry.Ignored.Add(1, new KeyValuePair<string, object?>("reason", moved.Error.Code));
            LogIgnored(logger, assigned.OrderId, from, moved.Error.Code);
            return;
        }

        db.AddToOutbox(
            new OrderStatusChangedV1(order.Id, order.MerchantId, order.MerchantOrderRef, from.ToString(), order.Status.ToString(),
                order.AssignedCourierId, Reason: null, now, order.Version),
            order.Id, order.Version, now);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CourierAssigned for order {OrderId} ignored: status is {Status} ({Code})")]
    private static partial void LogIgnored(ILogger logger, Guid orderId, OrderStatus status, string code);
}
