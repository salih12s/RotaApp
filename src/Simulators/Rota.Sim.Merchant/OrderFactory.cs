namespace Rota.Sim.Merchant;

/// <summary>The body of <c>POST /api/v1/orders</c> (docs/05 §1).</summary>
public sealed record NewOrderBody(string MerchantOrderRef, PlaceBody Pickup, PlaceBody Dropoff, CustomerBody Customer);

public sealed record PlaceBody(double Lat, double Lon, string Address);

public sealed record CustomerBody(string Name, string Phone);

/// <summary>
/// Makes believable orders: a pickup near Kadıköy and a dropoff a few kilometres away, always inside the 15 km service area
/// (an order the API would answer with 422 is no use to a simulator). Pure, seeded: the same seed gives the same orders.
/// </summary>
public static class OrderFactory
{
    public const double CentreLat = 40.9903;
    public const double CentreLon = 29.0290;

    // About 3 km in each direction; pickup and dropoff are at most ~8.5 km apart.
    private const double SpreadDegrees = 0.03;

    public static NewOrderBody Create(Random random, int merchantIndex, int orderIndex)
    {
        ArgumentNullException.ThrowIfNull(random);

        var pickup = Around(random);
        var dropoff = Around(random);
        return new NewOrderBody(
            $"SIM-{merchantIndex:D2}-{orderIndex:D5}-{random.Next(1000, 9999)}",
            new PlaceBody(pickup.Lat, pickup.Lon, $"Pickup {orderIndex}"),
            new PlaceBody(dropoff.Lat, dropoff.Lon, $"Dropoff {orderIndex}"),
            new CustomerBody($"Customer {orderIndex}", $"+9055500{orderIndex % 100000:D5}"));
    }

    private static (double Lat, double Lon) Around(Random random) =>
        (Math.Round(CentreLat + ((random.NextDouble() * 2) - 1) * SpreadDegrees, 5),
         Math.Round(CentreLon + ((random.NextDouble() * 2) - 1) * SpreadDegrees, 5));

    /// <summary>Great-circle distance in metres (haversine), for tests and sanity checks.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
