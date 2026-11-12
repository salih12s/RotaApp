using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Primitives;
using Rota.Contracts.Events.Dispatch;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Offering;

/// <summary>What the courier gets back after answering an offer.</summary>
public sealed record OfferAnswer(Guid OfferId, Guid OrderId, string Status);

/// <summary>
/// The courier's answer to an offer. The deciding statement is
/// <c>UPDATE offers SET status = 'Accepted' WHERE id = … AND status = 'Pending' AND expires_at &gt; now</c>:
/// the timeout job uses <c>status = 'Pending' AND expires_at &lt;= now</c>. Both start from <c>Pending</c>, so whichever
/// commits first changes the row and the other finds 0 rows; one offer can never be both accepted and expired.
/// </summary>
public sealed class OfferResponseService(DispatchDbContext db, IOptions<DispatchOptions> options, TimeProvider time)
{
    public Task<Result<OfferAnswer>> AcceptAsync(Guid offerId, Guid courierId, CancellationToken cancellationToken) =>
        RespondAsync(offerId, courierId, accept: true, cancellationToken);

    public Task<Result<OfferAnswer>> RejectAsync(Guid offerId, Guid courierId, CancellationToken cancellationToken) =>
        RespondAsync(offerId, courierId, accept: false, cancellationToken);

    private async Task<Result<OfferAnswer>> RespondAsync(Guid offerId, Guid courierId, bool accept, CancellationToken cancellationToken)
    {
        // Ownership is part of the lookup: someone else's offer is "not found", never "forbidden" (docs/06 §2).
        var offer = await db.Offers.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == offerId && o.CourierId == courierId, cancellationToken);
        if (offer is null)
        {
            return DispatchErrors.NotFound("offer");
        }

        var now = time.GetUtcNow();
        var target = accept ? OfferStatus.Accepted : OfferStatus.Rejected;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var answered = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE offers
            SET status = {target.ToString()}, responded_at = {now}
            WHERE id = {offerId} AND courier_id = {courierId} AND status = 'Pending' AND expires_at > {now}
            """, cancellationToken);
        if (answered == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await WhyNotAnswerableAsync(offerId, cancellationToken);
        }

        if (accept)
        {
            await CompleteAsync(offer, now, cancellationToken);
        }
        else
        {
            await db.CloseDeclinedOfferAsync(offer, options.Value.Rules, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OfferAnswer(offerId, offer.OrderId, target.ToString());
    }

    // The offer is ours now, so its job is Offering and its courier is holding it. Anything else is a bug, and throwing
    // rolls the whole transaction back instead of leaving an accepted offer without an assignment.
    private async Task CompleteAsync(Offer offer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = await db.TransitionJobAsync(offer.OrderId, j => j.Complete(now), cancellationToken);
        if (job.IsFailure)
        {
            throw new InvalidOperationException($"Offer {offer.Id} was accepted but its job {offer.OrderId} could not complete: {job.Error.Code}.");
        }

        if (!await db.TryAcceptCourierAsync(offer.CourierId, offer.Id, now, cancellationToken))
        {
            throw new InvalidOperationException($"Offer {offer.Id} was accepted but courier {offer.CourierId} was not holding it.");
        }

        await db.AddCourierStateChangedAsync(offer.CourierId, now, cancellationToken);
        db.AddToOutbox(new CourierAssignedV1(offer.OrderId, offer.CourierId, offer.Id, "Auto", offer.EtaSeconds, now),
            offer.OrderId, aggregateVersion: 1, now);
    }

    private async Task<Result<OfferAnswer>> WhyNotAnswerableAsync(Guid offerId, CancellationToken cancellationToken)
    {
        var current = await db.Offers.AsNoTracking().SingleAsync(o => o.Id == offerId, cancellationToken);
        return current.Status == OfferStatus.Pending ? DispatchErrors.OfferExpired : DispatchErrors.OfferNotPending(current.Status);
    }
}
