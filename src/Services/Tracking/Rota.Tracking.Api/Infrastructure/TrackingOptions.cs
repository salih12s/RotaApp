namespace Rota.Tracking.Api.Infrastructure;

/// <summary>Tracking settings, section <c>Tracking</c>. Defaults are the production values.</summary>
public sealed class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>
    /// Capacity of the queue between the gRPC streams and the history writer. When it is full, a stream
    /// waits for room instead of the memory growing without bound (back-pressure). Sized for a few
    /// seconds of the whole fleet.
    /// </summary>
    public int HistoryQueueCapacity { get; set; } = 10_000;

    /// <summary>A batch is written when it reaches this many points...</summary>
    public int HistoryBatchSize { get; set; } = 1_000;

    /// <summary>...or when its first point has waited this long, whichever comes first.</summary>
    public TimeSpan HistoryMaxWait { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A point whose device time is older than this is refused: its day may have no partition any more.
    /// Must stay within the partitions the maintenance job keeps (yesterday and later).
    /// </summary>
    public TimeSpan MaxPointAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>A device clock may run a little ahead; further than this it is refused.</summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Daily partitions are kept this long (KVKK retention, docs/04).</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Partitions are created this many days ahead, so midnight never finds one missing.</summary>
    public int PartitionDaysAhead { get; set; } = 3;

    /// <summary>How often the maintenance job runs (it is idempotent, so more often is harmless).</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);
}
