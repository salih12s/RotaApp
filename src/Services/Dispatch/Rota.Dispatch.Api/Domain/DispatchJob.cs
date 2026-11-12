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
    Postpone,
    Restart,
    ManualAssign,
}

/// <summary>
/// Which actions a job accepts in which status (docs/02-domain.md §5.4). Two actions have a
/// rule-dependent target, chosen in <see cref="DispatchJob"/>:
/// OfferDeclined → Searching, or Unassigned after the last attempt;
/// NoCandidatesFound → Searching (retry later), or Unassigned after the search timeout.
/// Postpone (a dependency such as Tracking failed) keeps the job in Searching and only moves the retry time.
/// ManualAssign: a dispatcher picks the courier, skipping the offer (only while no offer is open).
/// </summary>
public static class DispatchJobTransitions
{
    private static readonly FrozenSet<(DispatchJobStatus From, DispatchJobAction Action)> _allowed =
        new HashSet<(DispatchJobStatus, DispatchJobAction)>
        {
            (DispatchJobStatus.Searching, DispatchJobAction.StartOffering),
            (DispatchJobStatus.Searching, DispatchJobAction.NoCandidatesFound),
            (DispatchJobStatus.Searching, DispatchJobAction.Cancel),
            (DispatchJobStatus.Searching, DispatchJobAction.Postpone),
            (DispatchJobStatus.Searching, DispatchJobAction.ManualAssign),
            (DispatchJobStatus.Offering, DispatchJobAction.OfferDeclined),
            (DispatchJobStatus.Offering, DispatchJobAction.Complete),
            (DispatchJobStatus.Offering, DispatchJobAction.Cancel),
            (DispatchJobStatus.Unassigned, DispatchJobAction.Restart),
            (DispatchJobStatus.Unassigned, DispatchJobAction.ManualAssign),
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

/// <summary>A pickup or dropoff point as Dispatch needs it to build an offer (copied from <c>OrderCreated.v1</c>).</summary>
public sealed record JobPlace(double Lat, double Lon, string Address);

/// <summary>The search for a courier for one order. Progresses independently of any courier.</summary>
public sealed class DispatchJob
{
#pragma warning disable CS8618 // EF Core materialization only; every property is set from the row.
    private DispatchJob() { }
#pragma warning restore CS8618

    private DispatchJob(Guid orderId, string merchantId, JobPlace pickup, JobPlace dropoff, DateTimeOffset now)
    {
        OrderId = orderId;
        MerchantId = merchantId;
        PickupLatitude = pickup.Lat;
        PickupLongitude = pickup.Lon;
        PickupAddress = pickup.Address;
        DropoffLatitude = dropoff.Lat;
        DropoffLongitude = dropoff.Lon;
        DropoffAddress = dropoff.Address;
        Status = DispatchJobStatus.Searching;
        SearchStartedAt = now;
        UpdatedAt = now;
    }

    public Guid OrderId { get; }

    public string MerchantId { get; }

    public double PickupLatitude { get; }

    public double PickupLongitude { get; }

    public string PickupAddress { get; }

    public double DropoffLatitude { get; }

    public double DropoffLongitude { get; }

    public string DropoffAddress { get; }

    public DispatchJobStatus Status { get; private set; }

    public int OfferAttempts { get; private set; }

    public DateTimeOffset SearchStartedAt { get; private set; }

    /// <summary>When to search again after finding no candidates; null means "now".</summary>
    public DateTimeOffset? NextSearchAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DispatchJob Start(Guid orderId, string merchantId, JobPlace pickup, JobPlace dropoff, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        ArgumentNullException.ThrowIfNull(pickup);
        ArgumentNullException.ThrowIfNull(dropoff);
        return new DispatchJob(orderId, merchantId, pickup, dropoff, now);
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

    /// <summary>A dependency (Tracking, Redis) failed: the job stays in Searching and is tried again after the retry interval.</summary>
    public Result Postpone(DispatchRules rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var result = Move(DispatchJobAction.Postpone, DispatchJobStatus.Searching, now);
        if (result.IsSuccess)
        {
            NextSearchAt = now + rules.SearchRetryInterval;
        }

        return result;
    }

    /// <summary>A dispatcher restarts a job that gave up: a fresh search with a fresh attempt budget and timeout.</summary>
    public Result Restart(DateTimeOffset now)
    {
        var result = Move(DispatchJobAction.Restart, DispatchJobStatus.Searching, now);
        if (result.IsSuccess)
        {
            OfferAttempts = 0;
            SearchStartedAt = now;
            NextSearchAt = null;
        }

        return result;
    }

    /// <summary>A dispatcher assigned a courier directly; no offer exists, so the job completes from Searching or Unassigned.</summary>
    public Result ManualAssign(DateTimeOffset now)
    {
        var result = Move(DispatchJobAction.ManualAssign, DispatchJobStatus.Completed, now);
        if (result.IsSuccess)
        {
            NextSearchAt = null;
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
