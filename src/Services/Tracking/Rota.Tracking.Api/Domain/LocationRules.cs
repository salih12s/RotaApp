using Rota.BuildingBlocks.Primitives;

namespace Rota.Tracking.Api.Domain;

/// <summary>The rules a reported position must satisfy before it is accepted. Pure: no Redis, no gRPC types.</summary>
public static class LocationRules
{
    /// <summary>
    /// Redis GEO stores positions in a Mercator-style projection and refuses latitudes beyond this
    /// (about 85.05°). Istanbul is far from it; stricter than the usual ±90 on purpose, so a bad value
    /// is rejected here as the client's mistake instead of failing inside Redis as a server error.
    /// </summary>
    public const double MaxLatitude = 85.05112878;

    public const double MaxLongitude = 180;

    public static Error InvalidPoint(string reason) => new("validation-failed", reason);

    /// <param name="seq">Sequence number kept on the courier's device; starts at 1.</param>
    /// <param name="lat">Latitude in degrees.</param>
    /// <param name="lon">Longitude in degrees.</param>
    /// <param name="accuracyMeters">GPS accuracy; 0 means "not reported".</param>
    /// <param name="recordedAt">The device's clock; <c>null</c> when the message had none.</param>
    /// <param name="now">Server time.</param>
    /// <param name="maxAge">Older points are refused: their day may no longer have a history partition.</param>
    /// <param name="maxClockSkew">A device clock may run slightly ahead; beyond this the point is refused.</param>
    public static Result Validate(
        long seq, double lat, double lon, float accuracyMeters, DateTimeOffset? recordedAt, DateTimeOffset now, TimeSpan maxAge, TimeSpan maxClockSkew)
    {
        // Written so that NaN fails too (every comparison with NaN is false).
        if (!(lat is >= -MaxLatitude and <= MaxLatitude))
        {
            return InvalidPoint($"lat must be within [-{MaxLatitude}, {MaxLatitude}].");
        }

        if (!(lon is >= -MaxLongitude and <= MaxLongitude))
        {
            return InvalidPoint($"lon must be within [-{MaxLongitude}, {MaxLongitude}].");
        }

        if (seq < 1)
        {
            return InvalidPoint("seq must be positive.");
        }

        if (!(accuracyMeters >= 0))
        {
            return InvalidPoint("accuracy_m must be zero or positive.");
        }

        if (recordedAt is not { } recorded)
        {
            return InvalidPoint("recorded_at is required.");
        }

        if (recorded < now - maxAge)
        {
            return InvalidPoint($"recorded_at is older than {maxAge.TotalHours:0.#} hours.");
        }

        return recorded > now + maxClockSkew
            ? InvalidPoint("recorded_at is in the future.")
            : Result.Success();
    }
}
