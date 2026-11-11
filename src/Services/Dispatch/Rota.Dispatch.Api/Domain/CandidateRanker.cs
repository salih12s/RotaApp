namespace Rota.Dispatch.Api.Domain;

/// <summary>
/// A courier near the pickup. Distance and location time come from Tracking (GetNearbyCouriers),
/// which already drops locations older than 30 s; the state comes from Dispatch's own courier table.
/// </summary>
public sealed record Candidate(Guid CourierId, CourierState State, int DistanceMeters, DateTimeOffset LocationRecordedAt);

public static class CandidateRanker
{
    /// <summary>
    /// Only <see cref="CourierState.Available"/> couriers, nearest first; on equal distance the freshest
    /// location wins (its position is more trustworthy); CourierId breaks remaining ties so the order is deterministic.
    /// </summary>
    public static IReadOnlyList<Candidate> Rank(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return [.. candidates
            .Where(c => c.State == CourierState.Available)
            .OrderBy(c => c.DistanceMeters)
            .ThenByDescending(c => c.LocationRecordedAt)
            .ThenBy(c => c.CourierId)];
    }
}

/// <summary>Straight-line ETA at a constant average speed (ADR-008; default 20 km/h from the PRD).</summary>
public sealed class EtaCalculator
{
    public const double DefaultAverageSpeedKmh = 20;

    private readonly double _metersPerSecond;

    public EtaCalculator(double averageSpeedKmh = DefaultAverageSpeedKmh)
    {
        if (!double.IsFinite(averageSpeedKmh) || averageSpeedKmh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageSpeedKmh), averageSpeedKmh, "Speed must be a positive number.");
        }

        _metersPerSecond = averageSpeedKmh * 1000 / 3600;
    }

    /// <summary>Whole seconds, rounded up: promising 59 s for a 59.4 s trip would be early.</summary>
    public int EstimateSeconds(int distanceMeters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distanceMeters);
        return (int)Math.Ceiling(distanceMeters / _metersPerSecond);
    }
}
