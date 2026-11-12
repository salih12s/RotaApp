using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.Consumers;

/// <summary>
/// <c>DispatchFailed.v1</c> → <c>Dispatching → Unassigned</c> and <c>OrderStatusChanged.v1</c> in the outbox (the failure
/// reason travels as the status change's reason). A transition the order no longer allows is acknowledged and counted.
/// </summary>
public sealed partial class DispatchFailedHandler(OrdersDbContext db, TimeProvider time, ILogger<DispatchFailedHandler> logger)
    : IMessageHandler<DispatchFailedV1>
{
    public async Task HandleAsync(EventEnvelope<DispatchFailedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var failed = message.Data;

        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == failed.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {failed.OrderId} does not exist, but Dispatch reported a failure for it.");

        var now = time.GetUtcNow();
        var from = order.Status;
        var moved = order.MarkUnassigned(OrderActor.System, now);
        if (moved.IsFailure)
        {
            MessagingTelemetry.Ignored.Add(1, new KeyValuePair<string, object?>("reason", moved.Error.Code));
            LogIgnored(logger, failed.OrderId, from, moved.Error.Code);
            return;
        }

        db.AddToOutbox(
            new OrderStatusChangedV1(order.Id, order.MerchantId, order.MerchantOrderRef, from.ToString(), order.Status.ToString(),
                order.AssignedCourierId, failed.Reason, now, order.Version),
            order.Id, order.Version, now);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DispatchFailed for order {OrderId} ignored: status is {Status} ({Code})")]
    private static partial void LogIgnored(ILogger logger, Guid orderId, OrderStatus status, string code);
}
