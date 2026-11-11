using Rota.Sim.CourierFleet;

namespace Rota.Tracking.UnitTests.Simulator;

public sealed class FleetSimulatorTests
{
    [Fact]
    public void RouteWalker_StaysInsideIstanbul_ForAWholeDayOfSteps()
    {
        var walker = new RouteWalker(new Random(42));

        for (var i = 0; i < 30_000; i++)   // 3 s steps: more than a day of riding
        {
            var (lat, lon) = walker.Step(TimeSpan.FromSeconds(3));

            lat.ShouldBeInRange(RouteWalker.MinLat, RouteWalker.MaxLat);
            lon.ShouldBeInRange(RouteWalker.MinLon, RouteWalker.MaxLon);
        }
    }

    [Fact]
    public void RouteWalker_NeverMovesFasterThanTheCourier()
    {
        var walker = new RouteWalker(new Random(7), speedMetersPerSecond: 8);
        var (lat, lon) = (walker.Lat, walker.Lon);

        for (var i = 0; i < 2_000; i++)
        {
            var (nextLat, nextLon) = walker.Step(TimeSpan.FromSeconds(3));

            // 8 m/s for 3 s is 24 m; a step that turns a corner covers less, never more (plus rounding).
            RouteWalker.DistanceMeters(lat, lon, nextLat, nextLon).ShouldBeLessThanOrEqualTo(24.5);
            (lat, lon) = (nextLat, nextLon);
        }
    }

    [Fact]
    public void RouteWalker_ActuallyTravels()
    {
        var walker = new RouteWalker(new Random(1));
        var (startLat, startLon) = (walker.Lat, walker.Lon);

        for (var i = 0; i < 1_000; i++)
        {
            walker.Step(TimeSpan.FromSeconds(3));
        }

        RouteWalker.DistanceMeters(startLat, startLon, walker.Lat, walker.Lon).ShouldBeGreaterThan(100);
    }

    [Fact]
    public void RouteWalker_SameSeed_SameRoute()
    {
        var a = new RouteWalker(new Random(99));
        var b = new RouteWalker(new Random(99));

        for (var i = 0; i < 100; i++)
        {
            a.Step(TimeSpan.FromSeconds(3)).ShouldBe(b.Step(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void CourierIdentity_IsStablePerSeedAndIndex_AndDiffersBetweenCouriers()
    {
        FleetRunner.CourierIdentity(1, 5).ShouldBe(FleetRunner.CourierIdentity(1, 5));
        FleetRunner.CourierIdentity(1, 5).ShouldNotBe(FleetRunner.CourierIdentity(1, 6));
        FleetRunner.CourierIdentity(1, 5).ShouldNotBe(FleetRunner.CourierIdentity(2, 5));
    }

    [Fact]
    public void FleetOptions_Parse_TheCommandOfTheRoadmap()
    {
        var options = FleetOptions.Parse(["--couriers", "200", "--interval", "3"]);

        options.ShouldSatisfyAllConditions(
            o => o.Couriers.ShouldBe(200),
            o => o.IntervalSeconds.ShouldBe(3),
            o => o.Url.ShouldBe(new Uri("http://localhost:5203")));
    }

    [Fact]
    public void FleetOptions_Parse_DecimalsFlagsAndRates()
    {
        var options = FleetOptions.Parse(["--interval", "0.25", "--probe-nearby", "--disconnect-rate", "0.01", "--reorder-rate", "0.05", "--duration", "60"]);

        options.ShouldSatisfyAllConditions(
            o => o.IntervalSeconds.ShouldBe(0.25),
            o => o.ProbeNearby.ShouldBeTrue(),
            o => o.DisconnectRate.ShouldBe(0.01),
            o => o.ReorderRate.ShouldBe(0.05),
            o => o.DurationSeconds.ShouldBe(60));
    }

    [Theory]
    [InlineData("--couriers", "0")]
    [InlineData("--interval", "0")]
    [InlineData("--disconnect-rate", "1.5")]
    [InlineData("--nonsense", "1")]
    public void FleetOptions_Parse_RejectsNonsense(string name, string value) =>
        Should.Throw<ArgumentException>(() => FleetOptions.Parse([name, value]));

    [Fact]
    public void Percentile_UsesNearestRank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        FleetStats.Percentile(sorted, 50).ShouldBe(5);
        FleetStats.Percentile(sorted, 95).ShouldBe(10);
        FleetStats.Percentile(sorted, 10).ShouldBe(1);
        FleetStats.Percentile([42], 99).ShouldBe(42);
    }
}
