using System.Globalization;
using Rota.Tracking.Api.Domain;
using StackExchange.Redis;

namespace Rota.Tracking.Api.Infrastructure;

public sealed record NearbyCourierResult(Guid CourierId, double DistanceMeters, DateTimeOffset LastSeenAt);

public sealed record LastLocationResult(Guid CourierId, double Lat, double Lon, DateTimeOffset RecordedAt);

/// <summary>Reads of the live position index: "who is near here?" and "where is this courier now?".</summary>
public sealed class CourierQueries(IConnectionMultiplexer redis)
{
    /// <summary>A search never pulls more than this many couriers out of Redis, however sparse the fresh ones are.</summary>
    private const int MaxScan = 5_000;

    /// <summary>
    /// Nearest-first search and freshness filter in ONE script. Doing the filter after a "nearest N" search
    /// would let stale couriers that happen to be close use up the N slots and hide fresh ones just a bit
    /// further away. So the script looks at the nearest few, keeps the fresh ones, and when too few are
    /// fresh it widens the window and looks again, until it has enough or Redis has no more to give.
    /// Returns rows of {courierId, distanceMeters, lastSeenMs}, nearest first.
    /// </summary>
    private static readonly LuaScript _nearby = LuaScript.Prepare(
        """
        local want = tonumber(@want)
        local cutoff = tonumber(@cutoffMs)
        local fetch = want * 3
        while true do
            local rows = redis.call('GEOSEARCH', @geoKey, 'FROMLONLAT', @lon, @lat, 'BYRADIUS', @radius, 'm', 'ASC', 'COUNT', fetch, 'WITHDIST')
            local fresh = {}
            for _, row in ipairs(rows) do
                local seen = redis.call('ZSCORE', @seenKey, row[1])
                if seen and tonumber(seen) >= cutoff then
                    fresh[#fresh + 1] = { row[1], row[2], seen }
                    if #fresh == want then
                        break
                    end
                end
            end
            if #fresh == want or #rows < fetch or fetch >= tonumber(@maxScan) then
                return fresh
            end
            fetch = math.min(fetch * 4, tonumber(@maxScan))
        end
        """);

    /// <param name="query">The validated question.</param>
    /// <param name="now">Server time (from the TimeProvider): freshness is measured against it.</param>
    public async Task<IReadOnlyList<NearbyCourierResult>> NearbyAsync(NearbyQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);

        var raw = await redis.GetDatabase().ScriptEvaluateAsync(_nearby, new
        {
            geoKey = (RedisKey)CurrentPositionStore.GeoKey,
            seenKey = (RedisKey)CurrentPositionStore.LastSeenKey,
            lon = query.Lon,
            lat = query.Lat,
            radius = query.RadiusMeters,
            want = query.Limit,
            cutoffMs = (now - query.MaxAge).ToUnixTimeMilliseconds(),
            maxScan = MaxScan,
        });

        var rows = (RedisResult[])raw!;
        var results = new List<NearbyCourierResult>(rows.Length);
        foreach (var row in rows)
        {
            var parts = (RedisResult[])row!;
            results.Add(new NearbyCourierResult(
                Guid.Parse((string)parts[0]!),
                double.Parse((string)parts[1]!, CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds((long)double.Parse((string)parts[2]!, CultureInfo.InvariantCulture))));
        }

        return results;
    }

    /// <summary>The current position of each given courier; couriers Redis does not know are left out.</summary>
    public async Task<IReadOnlyList<LastLocationResult>> LastLocationsAsync(IReadOnlyList<Guid> courierIds)
    {
        ArgumentNullException.ThrowIfNull(courierIds);

        if (courierIds.Count == 0)
        {
            return [];
        }

        var db = redis.GetDatabase();
        var members = courierIds.Select(id => (RedisValue)id.ToString("D")).ToArray();

        // Two reads sent together (one round trip). Not atomic with each other, which is fine for a read:
        // a courier moving between them is at most one point (3 s) off.
        var positions = db.GeoPositionAsync(CurrentPositionStore.GeoKey, members);
        var recorded = db.HashGetAsync(CurrentPositionStore.LastRecordedKey, members);
        await Task.WhenAll(positions, recorded);

        var results = new List<LastLocationResult>(courierIds.Count);
        var geo = positions.Result;
        var times = recorded.Result;
        for (var i = 0; i < courierIds.Count; i++)
        {
            if (geo[i] is { } position && times[i].HasValue)
            {
                results.Add(new LastLocationResult(
                    courierIds[i], position.Latitude, position.Longitude, DateTimeOffset.FromUnixTimeMilliseconds((long)times[i])));
            }
        }

        return results;
    }
}
