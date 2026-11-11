using Rota.BuildingBlocks.Primitives;

namespace Rota.Tracking.Api.Domain;

/// <summary>A validated "who is near here?" question, with the protobuf defaults (0 = not set) filled in.</summary>
public sealed record NearbyQuery(double Lat, double Lon, int RadiusMeters, int Limit, TimeSpan MaxAge);

/// <summary>Limits and defaults of the query calls. Pure: no Redis, no gRPC types.</summary>
public static class QueryRules
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>The service area is 15 km (ADR-008); a search wider than this is a mistake, not a use case.</summary>
    public const int MaxRadiusMeters = 50_000;

    /// <summary>Same as the freshness rule of docs/02: a position older than this is not a candidate.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>Redis keeps last-seen times for five minutes (docs/04); asking for older than that cannot be answered.</summary>
    public static readonly TimeSpan MaxMaxAge = TimeSpan.FromMinutes(5);

    public const int MaxCourierIdsPerLookup = 1_000;

    /// <param name="lat">Search centre latitude.</param>
    /// <param name="lon">Search centre longitude.</param>
    /// <param name="radiusMeters">Required: there is no sensible default radius.</param>
    /// <param name="limit">0 means the default (20).</param>
    /// <param name="maxAgeSeconds">0 means the default (30 s).</param>
    public static Result<NearbyQuery> ValidateNearby(double lat, double lon, int radiusMeters, int limit, int maxAgeSeconds)
    {
        if (!(lat is >= -LocationRules.MaxLatitude and <= LocationRules.MaxLatitude)
            || !(lon is >= -LocationRules.MaxLongitude and <= LocationRules.MaxLongitude))
        {
            return LocationRules.InvalidPoint("lat/lon are outside the range Redis GEO supports.");
        }

        if (radiusMeters is < 1 or > MaxRadiusMeters)
        {
            return LocationRules.InvalidPoint($"radius_m must be within [1, {MaxRadiusMeters}].");
        }

        if (limit is < 0 or > MaxLimit)
        {
            return LocationRules.InvalidPoint($"limit must be within [1, {MaxLimit}] (0 = default {DefaultLimit}).");
        }

        var maxAge = maxAgeSeconds == 0 ? DefaultMaxAge : TimeSpan.FromSeconds(maxAgeSeconds);
        if (maxAgeSeconds < 0 || maxAge > MaxMaxAge)
        {
            return LocationRules.InvalidPoint($"max_age_seconds must be within [1, {MaxMaxAge.TotalSeconds:0}] (0 = default {DefaultMaxAge.TotalSeconds:0}).");
        }

        return new NearbyQuery(lat, lon, radiusMeters, limit == 0 ? DefaultLimit : limit, maxAge);
    }

    public static Result<IReadOnlyList<Guid>> ValidateCourierIds(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count > MaxCourierIdsPerLookup)
        {
            return LocationRules.InvalidPoint($"At most {MaxCourierIdsPerLookup} courier ids per call.");
        }

        var parsed = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return LocationRules.InvalidPoint($"'{id}' is not a courier id (GUID).");
            }

            parsed.Add(guid);
        }

        return parsed;
    }
}
