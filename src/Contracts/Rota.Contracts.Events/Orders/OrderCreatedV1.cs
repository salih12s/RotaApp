namespace Rota.Contracts.Events.Orders;

/// <summary>
/// <c>OrderCreated.v1</c>: a merchant created an order. Carries no customer data (KVKK):
/// consumers only need where and how far.
/// </summary>
public sealed record OrderCreatedV1(
    Guid OrderId,
    string MerchantId,
    string MerchantOrderRef,
    LocationV1 Pickup,
    LocationV1 Dropoff,
    int DistanceMeters,
    DateTimeOffset CreatedAt,
    int OrderVersion) : IIntegrationEvent
{
    public static string EventType => "OrderCreated.v1";

    public static string RoutingKey => "orders.order-created.v1";
}

public sealed record LocationV1(double Lat, double Lon, string Address);
