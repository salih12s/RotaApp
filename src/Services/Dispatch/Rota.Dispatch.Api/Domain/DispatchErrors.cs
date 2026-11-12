using Rota.BuildingBlocks.Primitives;

namespace Rota.Dispatch.Api.Domain;

/// <summary>Business errors of the Dispatch domain; codes match docs/06-security-and-errors.md §5.</summary>
public static class DispatchErrors
{
    // docs/06 §5: these three carry the courier's current state as an extra field.
    private static Dictionary<string, object?> StateDetail(CourierState current) => new() { ["currentState"] = current.ToString() };

    public static Error InvalidCourierTransition(CourierState current, CourierAction action) =>
        new("invalid-courier-transition", $"A courier in state '{current}' does not allow '{action}'.", StateDetail(current));

    public static Error CourierBusy(CourierState current) =>
        new("courier-busy", $"The shift cannot end while the courier is '{current}'.", StateDetail(current));

    public static Error CourierNotAvailable(CourierState current) =>
        new("courier-not-available", $"The courier is '{current}', not 'Available'.", StateDetail(current));

    public static Error OfferMismatch { get; } =
        new("invalid-courier-transition", "The offer or order does not belong to the courier's current work.");

    public static Error OfferNotPending(OfferStatus current) =>
        new("offer-not-pending", $"The offer is '{current}' and can no longer be answered.");

    public static Error OfferExpired { get; } =
        new("offer-not-pending", "The offer has expired.");

    /// <summary>Internal only (the timeout job's own query excludes it); never returned over HTTP.</summary>
    public static Error OfferNotExpired { get; } =
        new("offer-not-expired", "The offer has not reached its expiry time yet.");

    public static Error NotFound(string what) =>
        new("not-found", $"The {what} was not found.");

    /// <summary>A guarded UPDATE hit zero rows: someone else changed the row between our read and our write.</summary>
    public static Error ConcurrentChange { get; } =
        new("concurrency-conflict", "The resource was changed by another request; reload and retry.");

    public static Error JobNotAssignable(DispatchJobStatus current) =>
        new("job-not-assignable", $"The dispatch job is '{current}' and cannot be assigned or restarted by hand.");

    public static Error InvalidJobTransition(DispatchJobStatus current, DispatchJobAction action) =>
        new("invalid-job-transition", $"A dispatch job in status '{current}' does not allow '{action}'.");
}
