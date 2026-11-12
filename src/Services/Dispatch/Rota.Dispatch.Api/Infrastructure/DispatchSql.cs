using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Primitives;
using Rota.Contracts.Events.Dispatch;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>
/// Every state change in Dispatch is a conditional UPDATE: "change it, but only if it is still in the state I
/// expect", and "0 rows changed" means somebody else got there first. Reading a row, checking it in C# and
/// writing it back would let two requests both pass the check (docs/07 R-1; a FAIL signal of F05).
/// <para>
/// <b>Lock order: offer → job → courier.</b> Every flow that touches more than one of them takes the row locks in
/// this order, so two transactions can never wait for each other in a circle (a deadlock).
/// </para>
/// </summary>
internal static class DispatchSql
{
    // ---- Couriers ---------------------------------------------------------------------------------------------

    /// <summary>Available → Offered (one offer at a time, invariant I-1).</summary>
    public static async Task<bool> TryReserveCourierAsync(
        this DispatchDbContext db, Guid courierId, Guid offerId, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Offered', current_offer_id = {offerId}, current_order_id = {orderId},
                version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Available'
            """, cancellationToken) == 1;

    /// <summary>Offered → OnDelivery, only for the offer the courier is actually holding.</summary>
    public static async Task<bool> TryAcceptCourierAsync(
        this DispatchDbContext db, Guid courierId, Guid offerId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'OnDelivery', current_offer_id = NULL, version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Offered' AND current_offer_id = {offerId}
            """, cancellationToken) == 1;

    /// <summary>Offered → Available: the offer was rejected or expired.</summary>
    public static async Task<bool> TryReleaseCourierAsync(
        this DispatchDbContext db, Guid courierId, Guid offerId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Available', current_offer_id = NULL, current_order_id = NULL,
                version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Offered' AND current_offer_id = {offerId}
            """, cancellationToken) == 1;

    /// <summary>Available → OnDelivery by a dispatcher's decision (no offer).</summary>
    public static async Task<bool> TryAssignCourierManuallyAsync(
        this DispatchDbContext db, Guid courierId, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'OnDelivery', current_offer_id = NULL, current_order_id = {orderId},
                version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Available'
            """, cancellationToken) == 1;

    /// <summary>The order was delivered or returned: OnDelivery → Available. Returns the courier ids that were freed.</summary>
    public static async Task<IReadOnlyList<Guid>> FinishDeliveryForOrderAsync(
        this DispatchDbContext db, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var holders = await db.Couriers.AsNoTracking()
            .Where(c => c.CurrentOrderId == orderId && c.State == CourierState.OnDelivery)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Available', current_offer_id = NULL, current_order_id = NULL,
                version = version + 1, updated_at = {now}
            WHERE current_order_id = {orderId} AND state = 'OnDelivery'
            """, cancellationToken);
        return holders;
    }

    /// <summary>The order was cancelled: an offered or working courier is free again. Returns the courier ids that were freed.</summary>
    public static async Task<IReadOnlyList<Guid>> ReleaseCouriersOfCancelledOrderAsync(
        this DispatchDbContext db, Guid orderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var holders = await db.Couriers.AsNoTracking()
            .Where(c => c.CurrentOrderId == orderId && (c.State == CourierState.Offered || c.State == CourierState.OnDelivery))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Available', current_offer_id = NULL, current_order_id = NULL,
                version = version + 1, updated_at = {now}
            WHERE current_order_id = {orderId} AND state IN ('Offered', 'OnDelivery')
            """, cancellationToken);
        return holders;
    }

    /// <summary>
    /// Puts <c>CourierStateChanged.v1</c> in the outbox with the courier's row as it is now. Call it after the
    /// UPDATE inside the same transaction: the row is locked by us, so what we read is what we wrote.
    /// </summary>
    public static async Task AddCourierStateChangedAsync(
        this DispatchDbContext db, Guid courierId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.AsNoTracking().SingleAsync(c => c.Id == courierId, cancellationToken);
        db.AddToOutbox(
            new CourierStateChangedV1(courier.Id, courier.DisplayName, courier.State.ToString(), courier.CurrentOrderId, now, courier.Version),
            courier.Id, courier.Version, now);
    }

    // ---- Dispatch jobs ----------------------------------------------------------------------------------------

    /// <summary>
    /// Moves a job with the domain rule <paramref name="apply"/> (the domain decides the target status), then writes the
    /// result guarded by the status and attempt count it had when we read it. Fails with the domain's error when the
    /// rule refuses, or with <see cref="DispatchErrors.ConcurrentChange"/> when somebody else moved the job meanwhile.
    /// </summary>
    public static async Task<Result<DispatchJob>> TransitionJobAsync(
        this DispatchDbContext db, Guid orderId, Func<DispatchJob, Result> apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apply);

        var job = await db.DispatchJobs.AsNoTracking().SingleOrDefaultAsync(j => j.OrderId == orderId, cancellationToken);
        if (job is null)
        {
            return DispatchErrors.NotFound("dispatch job");
        }

        var fromStatus = job.Status.ToString();
        var fromAttempts = job.OfferAttempts;

        var applied = apply(job);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var written = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dispatch_jobs
            SET status = {job.Status.ToString()}, offer_attempts = {job.OfferAttempts},
                search_started_at = {job.SearchStartedAt}, next_search_at = {job.NextSearchAt}, updated_at = {job.UpdatedAt}
            WHERE order_id = {orderId} AND status = {fromStatus} AND offer_attempts = {fromAttempts}
            """, cancellationToken);

        return written == 1 ? job : DispatchErrors.ConcurrentChange;
    }

    /// <summary>
    /// The tail shared by "the offer was rejected" and "the offer expired": the job goes back to searching (or gives up
    /// after the last attempt), the courier is free again, and the outbox says so. The caller already closed the offer row.
    /// </summary>
    public static async Task CloseDeclinedOfferAsync(
        this DispatchDbContext db, Offer offer, DispatchRules rules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var job = await db.TransitionJobAsync(offer.OrderId, j => j.OfferDeclined(rules, now), cancellationToken);
        if (job.IsFailure)
        {
            // A pending offer exists only while its job is Offering; anything else is a bug, not a user error.
            throw new InvalidOperationException(
                $"Offer {offer.Id} was pending but its job {offer.OrderId} could not decline it: {job.Error.Code}.");
        }

        if (!await db.TryReleaseCourierAsync(offer.CourierId, offer.Id, now, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Offer {offer.Id} was pending but courier {offer.CourierId} was not holding it.");
        }

        await db.AddCourierStateChangedAsync(offer.CourierId, now, cancellationToken);

        if (job.Value.Status == DispatchJobStatus.Unassigned)
        {
            // One event per outcome; Orders orders its own changes by the order's version, not by this one.
            db.AddToOutbox(new DispatchFailedV1(offer.OrderId, "MaxAttemptsReached", now), offer.OrderId, aggregateVersion: 1, now);
        }
    }
}
