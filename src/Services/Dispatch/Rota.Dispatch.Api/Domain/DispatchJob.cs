using System.Collections.Frozen;
using Rota.BuildingBlocks.Primitives;

namespace Rota.Dispatch.Api.Domain;

public enum DispatchJobStatus
{
    Searching,
    Offering,
    Completed,
    Unassigned,
    Cancelled,
}

public enum DispatchJobAction
{
    StartOffering,
    OfferDeclined,
    NoCandidatesFound,
    Complete,
    Cancel,
}

/// <summary>
/// Which actions a job accepts in which status (docs/02-domain.md §5.4). Two actions have a
/// rule-dependent target, chosen in <see cref="DispatchJob"/>:
/// OfferDeclined → Searching, or Unassigned after the last attempt;
/// NoCandidatesFound → Searching (retry later), or Unassigned after the search timeout.
/// </summary>
public static class DispatchJobTransitions
{
    private static readonly FrozenSet<(DispatchJobStatus From, DispatchJobAction Action)> _allowed =
        new HashSet<(DispatchJobStatus, DispatchJobAction)>
        {
            (DispatchJobStatus.Searching, DispatchJobAction.StartOffering),
            (DispatchJobStatus.Searching, DispatchJobAction.NoCandidatesFound),
            (DispatchJobStatus.Searching, DispatchJobAction.Cancel),
            (DispatchJobStatus.Offering, DispatchJobAction.OfferDeclined),
            (DispatchJobStatus.Offering, DispatchJobAction.Complete),
            (DispatchJobStatus.Offering, DispatchJobAction.Cancel),
        }.ToFrozenSet();

    public static bool IsAllowed(DispatchJobStatus from, DispatchJobAction action) =>
        _allowed.Contains((from, action));
}

/// <summary>Configurable dispatch rules; defaults from docs/01-prd.md (business rules table).</summary>
public sealed record DispatchRules
{
    public static DispatchRules Default { get; } = new();

    public TimeSpan OfferTtl { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxOfferAttempts { get; init; } = 5;

    public TimeSpan SearchRetryInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>The search for a courier for one order. Progresses independently of any courier.</summary>
public sealed class DispatchJob
{
    private DispatchJob(Guid orderId, string merchantId, double pickupLatitude, double pickupLongitude, DateTimeOffset now)
    {
        OrderId = orderId;
        MerchantId = merchantId;
        PickupLatitude = pickupLatitude;
        PickupLongitude = pickupLongitude;
        Status = DispatchJobStatus.Searching;
        SearchStartedAt = now;
        UpdatedAt = now;
    }

    public Guid OrderId { get; }

    public string MerchantId { get; }

    public double PickupLatitude { get; }

    public double PickupLongitude { get; }

    public DispatchJobStatus Status { get; private set; }

    public int OfferAttempts { get; private set; }

    public DateTimeOffset SearchStartedAt { get; }

    /// <summary>When to search again after finding no candidates; null means "now".</summary>
    public DateTimeOffset? NextSearchAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DispatchJob Start(Guid orderId, string merchantId, double pickupLatitude, double pickupLongitude,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        return new DispatchJob(orderId, merchantId, pickupLatitude, pickupLongitude, now);
    }

    public Result StartOffering(DateTimeOffset now)
    {
        var result = Move(DispatchJobAction.StartOffering, DispatchJobStatus.Offering, now);
        if (result.IsSuccess)
        {
            OfferAttempts++;
            NextSearchAt = null;
        }

        return result;
    }

    /// <summary>The current offer was rejected or expired: try the next candidate, unless attempts ran out.</summary>
    public Result OfferDeclined(DispatchRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var target = OfferAttempts >= rules.MaxOfferAttempts ? DispatchJobStatus.Unassigned : DispatchJobStatus.Searching;
        return Move(DispatchJobAction.OfferDeclined, target, now);
    }

    /// <summary>No candidate nearby: retry after the interval, or give up after the search timeout.</summary>
    public Result NoCandidatesFound(DispatchRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var timedOut = now - SearchStartedAt >= rules.SearchTimeout;
        var result = Move(DispatchJobAction.NoCandidatesFound,
            timedOut ? DispatchJobStatus.Unassigned : DispatchJobStatus.Searching, now);
        if (result.IsSuccess)
        {
            NextSearchAt = timedOut ? null : now + rules.SearchRetryInterval;
        }

        return result;
    }

    public Result Complete(DateTimeOffset now) => Move(DispatchJobAction.Complete, DispatchJobStatus.Completed, now);

    public Result Cancel(DateTimeOffset now)
    {
        var result = Move(DispatchJobAction.Cancel, DispatchJobStatus.Cancelled, now);
        if (result.IsSuccess)
        {
            NextSearchAt = null;
        }

        return result;
    }

    private Result Move(DispatchJobAction action, DispatchJobStatus target, DateTimeOffset now)
    {
        if (!DispatchJobTransitions.IsAllowed(Status, action))
        {
            return DispatchErrors.InvalidJobTransition(Status, action);
        }

        Status = target;
        UpdatedAt = now;
        return Result.Success();
    }
}
