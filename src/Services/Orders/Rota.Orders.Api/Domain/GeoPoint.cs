using Rota.BuildingBlocks.Primitives;

namespace Rota.Orders.Api.Domain;

/// <summary>A WGS-84 coordinate in degrees. Value object: equal when both coordinates are equal.</summary>
public readonly record struct GeoPoint
{
    private GeoPoint(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public static Result<GeoPoint> Create(double latitude, double longitude)
    {
        // Written so that NaN fails too (every comparison with NaN is false).
        var valid = latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
        return valid ? new GeoPoint(latitude, longitude) : OrderErrors.InvalidCoordinates;
    }
}

/// <summary>Great-circle ("as the crow flies") distance; ADR-008.</summary>
public static class Distance
{
    /// <summary>Mean Earth radius (IUGG), the value most haversine calculators use.</summary>
    public const double EarthRadiusMeters = 6_371_000;

    public static double HaversineMeters(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLon = ToRadians(b.Longitude - a.Longitude);

        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
              + (Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        // Min guards against h drifting just above 1 through rounding (antipodal points).
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}

/// <summary>Pickup and dropoff may be at most <see cref="MaxDistanceMeters"/> apart (PRD: 15 km → 422).</summary>
public sealed class ServiceAreaPolicy
{
    public const int DefaultMaxDistanceMeters = 15_000;

    public ServiceAreaPolicy(int maxDistanceMeters = DefaultMaxDistanceMeters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDistanceMeters);
        MaxDistanceMeters = maxDistanceMeters;
    }

    public static ServiceAreaPolicy Default { get; } = new();

    public int MaxDistanceMeters { get; }

    /// <summary>Returns the rounded distance in meters, or <c>out-of-service-area</c>.</summary>
    public Result<int> CheckDistance(GeoPoint pickup, GeoPoint dropoff)
    {
        var meters = (int)Math.Round(Distance.HaversineMeters(pickup, dropoff), MidpointRounding.AwayFromZero);
        return meters <= MaxDistanceMeters ? meters : OrderErrors.OutOfServiceArea(meters, MaxDistanceMeters);
    }
}
