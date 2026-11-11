using System.Globalization;

namespace Rota.Tracking.Api.Domain;

/// <summary>
/// Which daily partitions of <c>location_points</c> must exist, and which are old enough to drop.
/// Pure date arithmetic (UTC): the maintenance job runs the SQL, this decides what to run.
/// </summary>
/// <remarks>
/// A partition holds one UTC day of <c>recorded_at</c>. Old data goes with <c>DROP TABLE</c> on a whole
/// partition: instant, and it leaves no dead rows behind, where <c>DELETE</c> of millions of rows is
/// slow and bloats the table (the 30-day retention of docs/04).
/// </remarks>
public static class PartitionPlan
{
    public const string TableName = "location_points";

    private const string NamePrefix = TableName + "_";

    /// <summary>
    /// Yesterday, today and the next <paramref name="daysAhead"/> days. Yesterday is needed because a
    /// courier that was offline may deliver points up to 24 hours old (the accepted window), and the
    /// upcoming days so a point never finds its partition missing at midnight.
    /// </summary>
    public static IReadOnlyList<DateOnly> RequiredDays(DateTimeOffset now, int daysAhead)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return [.. Enumerable.Range(-1, daysAhead + 2).Select(today.AddDays)];
    }

    /// <summary>A partition is dropped when its whole day (its upper bound) is older than the retention.</summary>
    public static bool IsExpired(DateOnly partitionDay, DateTimeOffset now, TimeSpan retention)
    {
        var upperBound = new DateTimeOffset(partitionDay.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return upperBound <= now - retention;
    }

    public static string NameFor(DateOnly day) => NamePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static bool TryParseDay(string partitionName, out DateOnly day)
    {
        day = default;
        return partitionName.StartsWith(NamePrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(partitionName[NamePrefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>Inclusive start and exclusive end of a day as UTC timestamps for the SQL bounds.</summary>
    public static (string From, string To) Bounds(DateOnly day) =>
        (Utc(day), Utc(day.AddDays(1)));

    private static string Utc(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00+00";
}
