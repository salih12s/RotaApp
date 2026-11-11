namespace Rota.Sim.CourierFleet;

/// <summary>
/// One pretend courier's movement: pick a random destination inside Istanbul, walk towards it at a
/// courier's speed, pick the next when it is reached. Straight lines, no roads: the point of the
/// simulator is a believable stream of coordinates, not a map.
/// </summary>
public sealed class RouteWalker
{
    // A box around the built-up area of Istanbul (both sides of the Bosphorus).
    public const double MinLat = 40.80;
    public const double MaxLat = 41.20;
    public const double MinLon = 28.60;
    public const double MaxLon = 29.45;

    private const double MetersPerDegreeLat = 111_194.9;

    private readonly Random _random;
    private readonly double _speedMetersPerSecond;
    private double _destinationLat;
    private double _destinationLon;

    public RouteWalker(Random random, double speedMetersPerSecond = 8)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speedMetersPerSecond);

        _random = random;
        _speedMetersPerSecond = speedMetersPerSecond;
        Lat = RandomBetween(MinLat, MaxLat);
        Lon = RandomBetween(MinLon, MaxLon);
        ChooseDestination();
    }

    public double Lat { get; private set; }

    public double Lon { get; private set; }

    /// <summary>Moves for <paramref name="elapsed"/> towards the destination (never past it) and returns the new position.</summary>
    public (double Lat, double Lon) Step(TimeSpan elapsed)
    {
        var remaining = _speedMetersPerSecond * elapsed.TotalSeconds;

        // A long step can finish a leg and start the next one.
        while (remaining > 0)
        {
            var toGo = DistanceMeters(Lat, Lon, _destinationLat, _destinationLon);
            if (toGo <= remaining)
            {
                (Lat, Lon) = (_destinationLat, _destinationLon);
                remaining -= toGo;
                ChooseDestination();
                continue;
            }

            var fraction = remaining / toGo;
            Lat += (_destinationLat - Lat) * fraction;
            Lon += (_destinationLon - Lon) * fraction;
            remaining = 0;
        }

        return (Lat, Lon);
    }

    /// <summary>Straight-line distance, flat-earth approximation: ample over a city (the error is far below GPS noise).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * MetersPerDegreeLat;
        var dLon = (lon2 - lon1) * MetersPerDegreeLat * Math.Cos((lat1 + lat2) / 2 * Math.PI / 180);
        return Math.Sqrt((dLat * dLat) + (dLon * dLon));
    }

    private void ChooseDestination()
    {
        _destinationLat = RandomBetween(MinLat, MaxLat);
        _destinationLon = RandomBetween(MinLon, MaxLon);
    }

    private double RandomBetween(double min, double max) => min + (_random.NextDouble() * (max - min));
}
