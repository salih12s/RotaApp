using Rota.Orders.Api.Domain;

namespace Rota.Orders.UnitTests.Domain;

public sealed class DistanceTests
{
    /// <summary>
    /// PASS criterion 3. Reference distances from an independent tool: geographiclib 2.1
    /// (Karney, WGS-84 ellipsoid geodesic, <c>Geodesic.WGS84.Inverse</c>), computed 2026-11-11.
    /// The ellipsoid and our sphere differ by up to ~0.5 %, which is exactly the tolerance.
    /// </summary>
    [Theory]
    [InlineData(40.9906, 29.0237, 40.9819, 29.0254, 976.7)]       // Kadıköy pier → Moda
    [InlineData(41.0422, 29.0067, 41.0260, 29.0150, 1_929.7)]     // Beşiktaş → Üsküdar
    [InlineData(41.0370, 28.9850, 40.8986, 29.3092, 31_320.4)]    // Taksim → Sabiha Gökçen
    [InlineData(41.0082, 28.9784, 39.9208, 32.8541, 350_144.6)]   // İstanbul → Ankara (Kızılay)
    public void HaversineMeters_MatchesIndependentGeodesic_WithinHalfPercent(
        double lat1, double lon1, double lat2, double lon2, double referenceMeters)
    {
        var a = GeoPoint.Create(lat1, lon1).Value;
        var b = GeoPoint.Create(lat2, lon2).Value;

        var meters = Distance.HaversineMeters(a, b);

        Math.Abs(meters - referenceMeters).ShouldBeLessThanOrEqualTo(referenceMeters * 0.005);
    }

    [Fact]
    public void HaversineMeters_SamePoint_IsZero()
    {
        var p = GeoPoint.Create(41.0, 29.0).Value;

        Distance.HaversineMeters(p, p).ShouldBe(0);
    }

    [Fact]
    public void HaversineMeters_IsSymmetric()
    {
        var a = GeoPoint.Create(41.0422, 29.0067).Value;
        var b = GeoPoint.Create(41.0260, 29.0150).Value;

        Distance.HaversineMeters(a, b).ShouldBe(Distance.HaversineMeters(b, a), tolerance: 1e-9);
    }

    [Theory]
    [InlineData(90.0001, 0)]
    [InlineData(-90.0001, 0)]
    [InlineData(0, 180.0001)]
    [InlineData(0, -180.0001)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void GeoPointCreate_OutOfRange_ReturnsValidationFailed(double latitude, double longitude) =>
        GeoPoint.Create(latitude, longitude).Error.ShouldNotBeNull().Code.ShouldBe("validation-failed");

    [Theory]
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    public void GeoPointCreate_OnTheEdge_Succeeds(double latitude, double longitude) =>
        GeoPoint.Create(latitude, longitude).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(15_000, true)]  // exactly the limit is allowed (≤ 15 km)
    [InlineData(15_001, false)]
    public void CheckDistance_AtTheLimit_AllowsOnlyUpToFifteenKm(int meters, bool allowed)
    {
        // Along a meridian the great-circle distance is exactly R · Δφ, so we can place points precisely.
        var pickup = GeoPoint.Create(41.0, 29.0).Value;
        var dropoff = GeoPoint.Create(41.0 + (meters / Distance.EarthRadiusMeters * 180 / Math.PI), 29.0).Value;

        var result = ServiceAreaPolicy.Default.CheckDistance(pickup, dropoff);

        if (allowed)
        {
            result.Value.ShouldBe(meters);
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBe("out-of-service-area");
        }
    }
}
