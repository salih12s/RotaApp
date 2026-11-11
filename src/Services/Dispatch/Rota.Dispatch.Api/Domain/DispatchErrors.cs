using Rota.BuildingBlocks.Primitives;

namespace Rota.Dispatch.Api.Domain;

/// <summary>Business errors of the Dispatch domain; codes match docs/06-security-and-errors.md §5.</summary>
public static class DispatchErrors
{
    public static Error InvalidCourierTransition(CourierState current, CourierAction action) =>
        new("invalid-courier-transition", $"A courier in state '{current}' does not allow '{action}'.");

    public static Error CourierBusy(CourierState current) =>
        new("courier-busy", $"The shift cannot end while the courier is '{current}'.");

    public static Error CourierNotAvailable(CourierState current) =>
        new("courier-not-available", $"The courier is '{current}', not 'Available'.");

    public static Error OfferMismatch { get; } =
        new("invalid-courier-transition", "The offer or order does not belong to the courier's current work.");

    public static Error OfferNotPending(OfferStatus current) =>
        new("offer-not-pending", $"The offer is '{current}' and can no longer be answered.");

    public static Error OfferExpired { get; } =
        new("offer-not-pending", "The offer has expired.");

    /// <summary>Internal only (the timeout job's own query excludes it); never returned over HTTP.</summary>
    public static Error OfferNotExpired { get; } =
        new("offer-not-expired", "The offer has not reached its expiry time yet.");

    public static Error InvalidJobTransition(DispatchJobStatus current, DispatchJobAction action) =>
        new("invalid-job-transition", $"A dispatch job in status '{current}' does not allow '{action}'.");
}
