namespace Rota.Tracking.IntegrationTests.Support;

/// <summary>
/// The "who is near here?" index is one Redis key shared by every test, so a courier seeded by one test
/// would show up in another's search. Each test therefore gets its own patch of the globe, far from the
/// others (at least a degree apart, ~55 km at the extremes of the grid; searches use at most 20 km).
/// </summary>
public static class TestArea
{
    /// <summary>Metres per degree of latitude on a sphere of the mean Earth radius.</summary>
    private const double MetersPerDegree = 111_194.9;

    private static int _next;

    /// <summary>The centre of a patch nobody else uses.</summary>
    public static (double Lat, double Lon) NextCenter()
    {
        var i = Interlocked.Increment(ref _next);
        return (-60 + (i % 100), -170 + ((i / 100) * 5));
    }

    /// <summary>A point the given number of metres due north of <paramref name="center"/>.</summary>
    public static (double Lat, double Lon) North((double Lat, double Lon) center, double meters) =>
        (center.Lat + (meters / MetersPerDegree), center.Lon);
}
