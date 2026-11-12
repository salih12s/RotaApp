using System.Collections.Frozen;
using Rota.BuildingBlocks.Primitives;

namespace Rota.Dispatch.Api.Domain;

public enum CourierState
{
    Offline,
    Available,
    Offered,
    OnDelivery,
}

public enum CourierAction
{
    StartShift,
    EndShift,
    ReceiveOffer,
    AcceptOffer,
    ReleaseOffer,
    FinishDelivery,
}

/// <summary>Courier transitions (docs/02-domain.md §5.2). Pairs missing here are rejected.</summary>
public static class CourierTransitions
{
    private static readonly FrozenDictionary<(CourierState From, CourierAction Action), CourierState> _table =
        new Dictionary<(CourierState, CourierAction), CourierState>
        {
            [(CourierState.Offline, CourierAction.StartShift)] = CourierState.Available,
            [(CourierState.Available, CourierAction.EndShift)] = CourierState.Offline,
            [(CourierState.Available, CourierAction.ReceiveOffer)] = CourierState.Offered,
            [(CourierState.Offered, CourierAction.AcceptOffer)] = CourierState.OnDelivery,
            [(CourierState.Offered, CourierAction.ReleaseOffer)] = CourierState.Available,       // reject / timeout / order cancelled
            [(CourierState.OnDelivery, CourierAction.FinishDelivery)] = CourierState.Available,  // delivered / returned / cancelled
        }.ToFrozenDictionary();

    public static bool TryGetTarget(CourierState from, CourierAction action, out CourierState to) =>
        _table.TryGetValue((from, action), out to);
}

/// <summary>
/// Courier aggregate: protects "one job at a time" (invariant I-1). In the database the same rule is
/// enforced again with a conditional update (<c>WHERE state = 'Available'</c>) because of races (F05).
/// </summary>
public sealed class Courier
{
#pragma warning disable CS8618 // EF Core materialization only; every property is set from the row.
    private Courier() { }
#pragma warning restore CS8618

    private Courier(Guid id, string displayName, DateTimeOffset now)
    {
        Id = id;
        DisplayName = displayName;
        State = CourierState.Offline;
        UpdatedAt = now;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public CourierState State { get; private set; }

    public Guid? CurrentOfferId { get; private set; }

    public Guid? CurrentOrderId { get; private set; }

    public DateTimeOffset? ShiftStartedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Counts state changes; <c>CourierStateChanged.v1</c> carries it so consumers drop stale events.</summary>
    public int Version { get; private set; }

    public static Courier Register(Guid id, string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new Courier(id, displayName, now);
    }

    public Result StartShift(DateTimeOffset now)
    {
        var result = TransitionTo(CourierAction.StartShift, now);
        if (result.IsSuccess)
        {
            ShiftStartedAt = now;
        }

        return result;
    }

    public Result EndShift(DateTimeOffset now)
    {
        var result = TransitionTo(CourierAction.EndShift, now);
        if (result.IsSuccess)
        {
            ShiftStartedAt = null;
        }

        return result;
    }

    public Result ReceiveOffer(Guid offerId, Guid orderId, DateTimeOffset now)
    {
        var result = TransitionTo(CourierAction.ReceiveOffer, now);
        if (result.IsSuccess)
        {
            CurrentOfferId = offerId;
            CurrentOrderId = orderId;
        }

        return result;
    }

    public Result AcceptOffer(Guid offerId, DateTimeOffset now)
    {
        if (State == CourierState.Offered && CurrentOfferId != offerId)
        {
            return DispatchErrors.OfferMismatch;
        }

        var result = TransitionTo(CourierAction.AcceptOffer, now);
        if (result.IsSuccess)
        {
            CurrentOfferId = null;
        }

        return result;
    }

    /// <summary>The offer ended without acceptance (rejected, expired or the order was cancelled).</summary>
    public Result ReleaseOffer(Guid offerId, DateTimeOffset now)
    {
        if (State == CourierState.Offered && CurrentOfferId != offerId)
        {
            return DispatchErrors.OfferMismatch;
        }

        var result = TransitionTo(CourierAction.ReleaseOffer, now);
        if (result.IsSuccess)
        {
            CurrentOfferId = null;
            CurrentOrderId = null;
        }

        return result;
    }

    /// <summary>The active order ended (delivered, returned or cancelled); the courier is free again.</summary>
    public Result FinishDelivery(Guid orderId, DateTimeOffset now)
    {
        if (State == CourierState.OnDelivery && CurrentOrderId != orderId)
        {
            return DispatchErrors.OfferMismatch;
        }

        var result = TransitionTo(CourierAction.FinishDelivery, now);
        if (result.IsSuccess)
        {
            CurrentOrderId = null;
        }

        return result;
    }

    private Result TransitionTo(CourierAction action, DateTimeOffset now)
    {
        if (!CourierTransitions.TryGetTarget(State, action, out var target))
        {
            return Rejection(action);
        }

        State = target;
        UpdatedAt = now;
        Version++;
        return Result.Success();
    }

    // The one place that picks the catalog code for a rejected courier action.
    private Error Rejection(CourierAction action) => action switch
    {
        CourierAction.EndShift when State is CourierState.Offered or CourierState.OnDelivery =>
            DispatchErrors.CourierBusy(State),
        CourierAction.ReceiveOffer => DispatchErrors.CourierNotAvailable(State),
        _ => DispatchErrors.InvalidCourierTransition(State, action),
    };
}
