using Rota.BuildingBlocks.Primitives;

namespace Rota.Dispatch.Api.Domain;

public enum OfferStatus
{
    Pending,
    Accepted,
    Rejected,
    Expired,
    Cancelled,
}

public enum OfferAction
{
    Accept,
    Reject,
    Expire,
    Cancel,
}

/// <summary>
/// An order offered to one courier for a limited time (docs/02-domain.md §5.3).
/// Every transition starts from <see cref="OfferStatus.Pending"/>. Accept/Reject require
/// <c>ExpiresAt &gt; now</c> and Expire requires <c>ExpiresAt &lt;= now</c>: the two conditions exclude
/// each other, so one offer can never be both accepted and expired. F05 uses the same conditions in SQL.
/// </summary>
public sealed class Offer
{
    private Offer(Guid id, Guid orderId, Guid courierId, int distanceMeters, int etaSeconds,
        DateTimeOffset now, DateTimeOffset expiresAt)
    {
        Id = id;
        OrderId = orderId;
        CourierId = courierId;
        DistanceMeters = distanceMeters;
        EtaSeconds = etaSeconds;
        Status = OfferStatus.Pending;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public Guid OrderId { get; }

    public Guid CourierId { get; }

    public int DistanceMeters { get; }

    public int EtaSeconds { get; }

    public OfferStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public static Offer Create(Guid id, Guid orderId, Guid courierId, int distanceMeters, int etaSeconds,
        DispatchRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfNegative(distanceMeters);
        ArgumentOutOfRangeException.ThrowIfNegative(etaSeconds);
        return new Offer(id, orderId, courierId, distanceMeters, etaSeconds, now, now + rules.OfferTtl);
    }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt <= now;

    public Result Accept(DateTimeOffset now) => Respond(OfferStatus.Accepted, now);

    public Result Reject(DateTimeOffset now) => Respond(OfferStatus.Rejected, now);

    public Result Expire(DateTimeOffset now)
    {
        if (Status != OfferStatus.Pending)
        {
            return DispatchErrors.OfferNotPending(Status);
        }

        if (!IsExpiredAt(now))
        {
            return DispatchErrors.OfferNotExpired;
        }

        Status = OfferStatus.Expired;
        return Result.Success();
    }

    /// <summary>The order was cancelled while the offer was open. Allowed until it is answered or expired.</summary>
    public Result Cancel()
    {
        if (Status != OfferStatus.Pending)
        {
            return DispatchErrors.OfferNotPending(Status);
        }

        Status = OfferStatus.Cancelled;
        return Result.Success();
    }

    private Result Respond(OfferStatus target, DateTimeOffset now)
    {
        if (Status != OfferStatus.Pending)
        {
            return DispatchErrors.OfferNotPending(Status);
        }

        // Still Pending in memory but past its time: the timeout job just has not run yet.
        if (IsExpiredAt(now))
        {
            return DispatchErrors.OfferExpired;
        }

        Status = target;
        RespondedAt = now;
        return Result.Success();
    }
}
