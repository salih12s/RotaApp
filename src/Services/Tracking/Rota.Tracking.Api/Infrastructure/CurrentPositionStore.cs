using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Rota.Tracking.Api.Infrastructure;

/// <summary>A position as stored and published; the courier id travels in the key, not in the gRPC message.</summary>
public sealed record CourierPosition(Guid CourierId, long Seq, double Lat, double Lon, DateTimeOffset RecordedAt);

/// <summary>
/// The courier's current position in Redis (docs/04 "Tracking Redis anahtarları"). Redis holds derived
/// data only: if it is wiped, the next position each courier sends (3 s) fills it again (ADR-001).
/// </summary>
public sealed class CurrentPositionStore(IConnectionMultiplexer redis)
{
    public const string GeoKey = "trk:geo:couriers";
    public const string LastSeenKey = "trk:lastseen";
    public const string LastSeqKey = "trk:lastseq";

    /// <summary>
    /// Device time (recorded_at) of the current position, courier id -> unix ms. Kept apart from
    /// <see cref="LastSeenKey"/>, which is server time: the first answers "when was it recorded", the
    /// second "is the courier still sending", and the two can differ by minutes (offline buffering).
    /// </summary>
    public const string LastRecordedKey = "trk:lastrec";

    public static string ChannelFor(Guid courierId) => $"trk:courier:{courierId:D}:location";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One script, run by Redis as a single indivisible step: "is this newer than what I have?", then
    /// write the position, the last-seen time and the sequence number, then publish. Redis runs nothing
    /// else in between, so two streams of the same courier can never interleave a check and a write and
    /// leave an older point as the current one (invariant I-8). Done as separate commands from C#, the
    /// check of one stream could slip between the check and the write of the other.
    /// Returns 1 when the point became the current position, 0 when it was not newer.
    /// </summary>
    private static readonly LuaScript _update = LuaScript.Prepare(
        """
        local last = redis.call('HGET', @seqKey, @id)
        if last and tonumber(last) >= tonumber(@seq) then
            return 0
        end
        redis.call('GEOADD', @geoKey, @lon, @lat, @id)
        redis.call('ZADD', @seenKey, @nowMs, @id)
        redis.call('HSET', @seqKey, @id, @seq)
        redis.call('HSET', @recKey, @id, @recMs)
        redis.call('PUBLISH', @channel, @payload)
        return 1
        """);

    /// <param name="position">The point to apply; the courier id comes from the authenticated principal.</param>
    /// <param name="now">Server time (from the TimeProvider), not Redis TIME: freshness is measured against it, and tests control it.</param>
    /// <returns><c>true</c> if it became the current position, <c>false</c> if it was not newer (an old or repeated point).</returns>
    public async Task<bool> TryUpdateAsync(CourierPosition position, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(position);

        var payload = JsonSerializer.Serialize(
            new { courierId = position.CourierId, lat = position.Lat, lon = position.Lon, recordedAt = position.RecordedAt, seq = position.Seq },
            _json);

        var result = await redis.GetDatabase().ScriptEvaluateAsync(_update, new
        {
            geoKey = (RedisKey)GeoKey,
            seenKey = (RedisKey)LastSeenKey,
            seqKey = (RedisKey)LastSeqKey,
            recKey = (RedisKey)LastRecordedKey,
            recMs = position.RecordedAt.ToUnixTimeMilliseconds(),
            id = position.CourierId.ToString("D", CultureInfo.InvariantCulture),
            seq = position.Seq,
            lat = position.Lat,
            lon = position.Lon,
            nowMs = now.ToUnixTimeMilliseconds(),
            channel = ChannelFor(position.CourierId),
            payload,
        });

        return (long)result == 1;
    }
}
