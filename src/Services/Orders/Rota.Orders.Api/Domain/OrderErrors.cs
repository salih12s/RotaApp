using Rota.BuildingBlocks.Primitives;

namespace Rota.Orders.Api.Domain;

/// <summary>Business errors of the Orders domain; codes match docs/06-security-and-errors.md §5.</summary>
public static class OrderErrors
{
    public static Error InvalidTransition(OrderStatus current, OrderAction action) =>
        new("invalid-order-transition", $"An order in status '{current}' does not allow '{action}'.", CurrentStatus(current));

    public static Error UnlockNotAllowed(OrderStatus current) =>
        new("invalid-order-transition", $"The delivery code can only be unlocked in status 'PickedUp', not '{current}'.", CurrentStatus(current));

    public static Error InvalidDeliveryCode { get; } =
        new("invalid-delivery-code", "The delivery code is wrong.");

    public static Error DeliveryCodeLocked { get; } =
        new("delivery-code-locked", "The delivery code is locked after too many wrong attempts; a dispatcher must unlock it.");

    public static Error MalformedDeliveryCode { get; } =
        new("validation-failed", $"The delivery code must be exactly {DeliveryCode.Length} digits.");

    public static Error InvalidCoordinates { get; } =
        new("validation-failed", "Latitude must be within [-90, 90] and longitude within [-180, 180].");

    // The catalog adds currentStatus to invalid-order-transition (docs/06 §5) so clients can resync.
    private static Dictionary<string, object?> CurrentStatus(OrderStatus current) =>
        new(StringComparer.Ordinal) { ["currentStatus"] = current.ToString() };

    public static Error OutOfServiceArea(int distanceMeters, int maxDistanceMeters) =>
        new("out-of-service-area", $"Pickup and dropoff are {distanceMeters} m apart; the limit is {maxDistanceMeters} m.");
}
