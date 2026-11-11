namespace Rota.Sim.CourierFleet;

/// <summary>Counters shared by every simulated courier. Thread-safe.</summary>
public sealed class FleetStats
{
    private readonly object _probeLock = new();
    private readonly List<double> _probeMilliseconds = [];

    private long _sent;
    private long _accepted;
    private long _outOfOrder;
    private long _streamErrors;
    private long _disconnects;
    private long _probeErrors;

    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>What the server reported when each stream ended: points that became the current position.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>What the server reported when each stream ended: points that were not newer than the current one.</summary>
    public long OutOfOrder => Interlocked.Read(ref _outOfOrder);

    public long StreamErrors => Interlocked.Read(ref _streamErrors);

    public long Disconnects => Interlocked.Read(ref _disconnects);

    public long ProbeErrors => Interlocked.Read(ref _probeErrors);

    public void PointSent() => Interlocked.Increment(ref _sent);

    public void StreamEnded(long accepted, long outOfOrder)
    {
        Interlocked.Add(ref _accepted, accepted);
        Interlocked.Add(ref _outOfOrder, outOfOrder);
    }

    public void StreamFailed() => Interlocked.Increment(ref _streamErrors);

    public void WentOffline() => Interlocked.Increment(ref _disconnects);

    public void ProbeFailed() => Interlocked.Increment(ref _probeErrors);

    public void ProbeAnswered(double milliseconds)
    {
        lock (_probeLock)
        {
            _probeMilliseconds.Add(milliseconds);
        }
    }

    /// <summary>Percentiles (p in 0..100) of the probe latencies so far; <c>null</c> when there are none.</summary>
    public (int Count, double P50, double P95, double P99, double Max)? ProbeSummary()
    {
        double[] sorted;
        lock (_probeLock)
        {
            sorted = [.. _probeMilliseconds.Order()];
        }

        return sorted.Length == 0 ? null : (sorted.Length, Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an already sorted array.</summary>
    public static double Percentile(double[] sorted, double percent)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        var rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
