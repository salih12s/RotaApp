using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.Consumers;

/// <summary>
/// <c>DispatchStarted.v1</c> → <c>Created/Unassigned → Dispatching</c> and <c>OrderStatusChanged.v1</c> in the outbox.
/// A transition the order no longer allows (e.g. it was cancelled meanwhile) is not an error: the event
/// is acknowledged and counted, never retried (docs/05 §3 "Kim neye tepki verir?").
/// </summary>
public sealed partial class DispatchStartedHandler(OrdersDbContext db, TimeProvider time, ILogger<DispatchStartedHandler> logger)
    : IMessageHandler<DispatchStartedV1>
{
    public async Task HandleAsync(EventEnvelope<DispatchStartedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var orderId = message.Data.OrderId;

        // Not filtered by merchant: this is a system consumer, not a caller acting for someone.
        // The order was committed before its OrderCreated event left the outbox, so a missing row is a bug:
        // throwing sends it through retry to the DLQ, where it gets looked at.
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {orderId} does not exist, but Dispatch reported it started.");

        var now = time.GetUtcNow();
        var from = order.Status;
        var moved = order.MarkDispatching(OrderActor.System, now);
        if (moved.IsFailure)
        {
            MessagingTelemetry.Ignored.Add(1, new KeyValuePair<string, object?>("reason", moved.Error.Code));
            LogIgnored(logger, orderId, from, moved.Error.Code);
            return;
        }

        // Same transaction as the status change (the consumer saves after this returns).
        db.AddToOutbox(
            new OrderStatusChangedV1(order.Id, order.MerchantId, order.MerchantOrderRef, from.ToString(), order.Status.ToString(),
                order.AssignedCourierId, Reason: null, now, order.Version),
            order.Id, order.Version, now);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DispatchStarted for order {OrderId} ignored: status is {Status} ({Code})")]
    private static partial void LogIgnored(ILogger logger, Guid orderId, OrderStatus status, string code);
}
