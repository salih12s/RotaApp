using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Orders;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Consumers;

/// <summary>
/// <c>OrderStatusChanged.v1</c> (docs/05 §3): <c>Cancelled</c> stops the job, withdraws an open offer and frees the courier;
/// <c>Delivered</c>/<c>Returned</c> free the courier. Every other status is not Dispatch's business.
/// Each step is a conditional update, so a redelivered or reordered event changes nothing the second time.
/// Lock order: offer → job → courier (see <see cref="DispatchSql"/>).
/// </summary>
public sealed class OrderStatusChangedHandler(DispatchDbContext db, OfferNotifier notifier, TimeProvider time)
    : IMessageHandler<OrderStatusChangedV1>
{
    public async Task HandleAsync(EventEnvelope<OrderStatusChangedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var change = message.Data;
        var now = time.GetUtcNow();

        switch (change.ToStatus)
        {
            case Cancelled:
                await CancelAsync(change.OrderId, now, cancellationToken);
                break;

            case Delivered or Returned:
                foreach (var courierId in await db.FinishDeliveryForOrderAsync(change.OrderId, now, cancellationToken))
                {
                    await db.AddCourierStateChangedAsync(courierId, now, cancellationToken);
                }

                break;
        }
    }

    private async Task CancelAsync(Guid orderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A cancellation that overtakes the order's own OrderCreated (different queues, no ordering between them)
        // finds no job yet. Failing sends the message through the retry queues; by the next attempt the job exists.
        if (!await db.DispatchJobs.AnyAsync(j => j.OrderId == orderId, cancellationToken))
        {
            throw new InvalidOperationException($"Order {orderId} was cancelled but Dispatch has no job for it yet; retrying later.");
        }

        // 1. The open offer, if any (offer lock first).
        var withdrawn = await db.Offers.AsNoTracking()
            .Where(o => o.OrderId == orderId && o.Status == OfferStatus.Pending)
            .Select(o => new { o.Id, o.CourierId })
            .ToListAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE offers SET status = 'Cancelled' WHERE order_id = {orderId} AND status = 'Pending'", cancellationToken);

        // 2. The job, only while it is still searching or offering. A completed job stays completed: the order
        //    was already assigned, and the courier below is what needs freeing.
        await db.TransitionJobAsync(orderId, j => j.Cancel(now), cancellationToken);

        // 3. The courier, offered or working on this order.
        foreach (var courierId in await db.ReleaseCouriersOfCancelledOrderAsync(orderId, now, cancellationToken))
        {
            await db.AddCourierStateChangedAsync(courierId, now, cancellationToken);
        }

        foreach (var offer in withdrawn)
        {
            db.AfterCommit(_ => notifier.NotifyAsync(offer.CourierId, offer.Id));
        }
    }

    // OrderStatus lives in the Orders service; Dispatch only compares the names that arrive in the event.
    private const string Cancelled = "Cancelled";
    private const string Delivered = "Delivered";
    private const string Returned = "Returned";
}
