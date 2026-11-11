using Rota.Orders.Api.Domain;

namespace Rota.Orders.Api.Features;

/// <summary>An order as read through the API. Never contains the delivery code or its hash.</summary>
public sealed record OrderResponse(
    Guid Id,
    string MerchantOrderRef,
    string Status,
    LocationResponse Pickup,
    LocationResponse Dropoff,
    CustomerResponse Customer,
    int DistanceMeters,
    Guid? AssignedCourierId,
    bool DeliveryCodeLocked,
    int OrderVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderResponse(
            order.Id,
            order.MerchantOrderRef,
            order.Status.ToString(),
            LocationResponse.From(order.PickupLocation),
            LocationResponse.From(order.DropoffLocation),
            new CustomerResponse(order.Customer.Name, order.Customer.Phone),
            order.DistanceMeters,
            order.AssignedCourierId,
            order.DeliveryCode.IsLocked,
            order.Version,
            order.CreatedAt,
            order.UpdatedAt);
    }
}

public sealed record LocationResponse(double Lat, double Lon, string Address)
{
    public static LocationResponse From(OrderLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new LocationResponse(location.Point.Latitude, location.Point.Longitude, location.Address);
    }
}

public sealed record CustomerResponse(string Name, string Phone);
