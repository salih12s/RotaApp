using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Offering;

public enum OfferingOutcome
{
    /// <summary>A courier now holds a pending offer for the order.</summary>
    Offered,

    /// <summary>Nobody suitable right now; the job waits (or gave up after the search timeout).</summary>
    NoCandidates,

    /// <summary>Tracking failed or was too slow; the job stays in Searching and is tried again later.</summary>
    Postponed,

    /// <summary>The job is gone or no longer searching (cancelled, assigned by hand, taken by another worker).</summary>
    Skipped,
}

/// <summary>
/// One search step for one dispatch job: ask Tracking who is near the pickup, rank the answer, and try to reserve the
/// best courier. The reservation is the only place a courier becomes "Offered", and it is a conditional UPDATE
/// (<see cref="DispatchSql.TryReserveCourierAsync"/>): of two jobs wanting the same courier exactly one wins, the other
/// moves on to the next candidate.
/// </summary>
public sealed class OfferingService(
    DispatchDbContext db,
    TrackingCourierLocator locator,
    OfferNotifier notifier,
    EtaCalculator eta,
    IOptions<DispatchOptions> options,
    TimeProvider time)
{
    public async Task<OfferingOutcome> TryOfferAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var job = await db.DispatchJobs.AsNoTracking().SingleOrDefaultAsync(j => j.OrderId == orderId, cancellationToken);
        if (job is null || job.Status != DispatchJobStatus.Searching)
        {
            return OfferingOutcome.Skipped;
        }

        var nearby = await locator.FindNearbyAsync(job.PickupLatitude, job.PickupLongitude, cancellationToken);
        if (nearby.IsFailure)
        {
            // Not "no candidates": we do not know. The job keeps its place and the search budget keeps running.
            DispatchTelemetry.SearchPostponed.Add(1);
            await db.TransitionJobAsync(orderId, j => j.Postpone(options.Value.Rules, time.GetUtcNow()), cancellationToken);
            return OfferingOutcome.Postponed;
        }

        foreach (var candidate in await RankAsync(orderId, nearby.Value, cancellationToken))
        {
            switch (await TryReserveAsync(orderId, candidate, cancellationToken))
            {
                case Reservation.Offered:
                    return OfferingOutcome.Offered;
                case Reservation.JobGone:
                    return OfferingOutcome.Skipped;
            }

            // CourierTaken: another job got this courier first. Try the next one.
        }

        await GiveNoCandidatesAsync(orderId, cancellationToken);
        return OfferingOutcome.NoCandidates;
    }

    // Tracking knows positions; only Dispatch knows who is on shift and free. Couriers unknown to Dispatch never started a shift.
    // A courier who already had an offer for this order (rejected, expired) is not asked again: "the next candidate" means
    // somebody else, otherwise the nearest courier who ignores offers would get all five attempts.
    private async Task<IReadOnlyList<Candidate>> RankAsync(Guid orderId, IReadOnlyList<NearbyCourierInfo> nearby, CancellationToken cancellationToken)
    {
        var ids = nearby.Select(n => n.CourierId).ToArray();
        var states = await db.Couriers.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.State, cancellationToken);
        var alreadyAsked = (await db.Offers.AsNoTracking()
                .Where(o => o.OrderId == orderId)
                .Select(o => o.CourierId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return CandidateRanker.Rank(nearby
            .Where(n => states.ContainsKey(n.CourierId) && !alreadyAsked.Contains(n.CourierId))
            .Select(n => new Candidate(n.CourierId, states[n.CourierId], n.DistanceMeters, n.LastSeenAt)));
    }

    private enum Reservation
    {
        Offered,
        CourierTaken,
        JobGone,
    }

    private async Task<Reservation> TryReserveAsync(Guid orderId, Candidate candidate, CancellationToken cancellationToken)
    {
        var rules = options.Value.Rules;
        var now = time.GetUtcNow();
        var offerId = Guid.CreateVersion7(now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock order is job, then courier (the offer row is new, nobody can be waiting for it).
        var started = await db.TransitionJobAsync(orderId, j => j.StartOffering(now), cancellationToken);
        if (started.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Reservation.JobGone;
        }

        if (!await db.TryReserveCourierAsync(candidate.CourierId, offerId, orderId, now, cancellationToken))
        {
            // Rolling back also takes the job's Searching → Offering step back.
            await transaction.RollbackAsync(cancellationToken);
            return Reservation.CourierTaken;
        }

        db.Offers.Add(Offer.Create(offerId, orderId, candidate.CourierId, candidate.DistanceMeters,
            eta.EstimateSeconds(candidate.DistanceMeters), rules, now));
        await db.AddCourierStateChangedAsync(candidate.CourierId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // Only after the commit: a push for an offer that was rolled back would show the courier something that never existed.
        db.AfterCommit(_ => notifier.NotifyAsync(candidate.CourierId, offerId));
        await transaction.CommitAsync(cancellationToken);

        DispatchTelemetry.OffersCreated.Add(1);
        if (started.Value.OfferAttempts == 1)
        {
            DispatchTelemetry.TimeToFirstOffer.Record((now - started.Value.SearchStartedAt).TotalSeconds);
        }

        return Reservation.Offered;
    }

    private async Task GiveNoCandidatesAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var rules = options.Value.Rules;
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var moved = await db.TransitionJobAsync(orderId, j => j.NoCandidatesFound(rules, now), cancellationToken);
        if (moved.IsFailure)
        {
            // Cancelled or assigned by hand while we searched: nothing left to report.
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (moved.Value.Status == DispatchJobStatus.Unassigned)
        {
            db.AddToOutbox(new DispatchFailedV1(orderId, "NoCandidates", now), orderId, aggregateVersion: 1, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
