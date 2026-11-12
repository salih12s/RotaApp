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

    // ---- Simulator v2: how a courier answers offers --------------------------------------------------------------------

    private static readonly OfferBehavior _sixtyTwentyFive = new(0.6, 0.25, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(0.0, OfferDecision.Accept)]
    [InlineData(0.59, OfferDecision.Accept)]
    [InlineData(0.6, OfferDecision.Reject)]   // the boundary belongs to the next bucket: accept is roll < 0.6
    [InlineData(0.84, OfferDecision.Reject)]
    [InlineData(0.85, OfferDecision.Ignore)]
    [InlineData(0.999, OfferDecision.Ignore)]
    public void OfferBehavior_Decide_FollowsTheRateBuckets(double roll, OfferDecision expected) =>
        _sixtyTwentyFive.Decide(roll).ShouldBe(expected);

    [Fact]
    public void OfferBehavior_Decide_OverManyRolls_MatchesTheConfiguredRates()
    {
        var random = new Random(2026);
        var counts = new Dictionary<OfferDecision, int>();
        for (var i = 0; i < 20_000; i++)
        {
            var decision = _sixtyTwentyFive.Decide(random.NextDouble());
            counts[decision] = counts.GetValueOrDefault(decision) + 1;
        }

        (counts[OfferDecision.Accept] / 20_000.0).ShouldBe(0.60, tolerance: 0.02);
        (counts[OfferDecision.Reject] / 20_000.0).ShouldBe(0.25, tolerance: 0.02);
        (counts[OfferDecision.Ignore] / 20_000.0).ShouldBe(0.15, tolerance: 0.02);
    }

    [Fact]
    public void OfferBehavior_AcceptEverything_NeverRejectsOrIgnores()
    {
        var always = new OfferBehavior(1, 0, TimeSpan.Zero, TimeSpan.Zero);

        Enumerable.Range(0, 100).Select(i => always.Decide(i / 100.0)).ShouldAllBe(d => d == OfferDecision.Accept);
    }

    [Fact]
    public void OfferBehavior_ThinkTime_StaysBetweenTheLimits()
    {
        _sixtyTwentyFive.ThinkTime(0).ShouldBe(TimeSpan.FromSeconds(0.5));
        _sixtyTwentyFive.ThinkTime(0.9999).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        _sixtyTwentyFive.ThinkTime(0.5).ShouldBe(TimeSpan.FromSeconds(2.75));
    }

    [Fact]
    public void FleetOptions_WithoutOffersFlag_BehavesLikeV1()
    {
        // F04's baseline measurement must not change: v2 is opt-in.
        FleetOptions.Parse(["--couriers", "10"]).Offers.ShouldBeFalse();
    }

    [Fact]
    public void FleetOptions_OfferSettings_AreParsed()
    {
        var options = FleetOptions.Parse(["--offers", "--accept-rate", "0.5", "--reject-rate", "0.3", "--response-delay", "2", "--dispatch-http", "http://dispatch:5102"]);

        options.Offers.ShouldBeTrue();
        options.AcceptRate.ShouldBe(0.5);
        options.RejectRate.ShouldBe(0.3);
        options.OfferBehavior.MaxThinkTime.ShouldBe(TimeSpan.FromSeconds(2));
        options.DispatchHttpUrl.ShouldBe(new Uri("http://dispatch:5102"));
    }

    [Theory]
    [InlineData("0.7", "0.4")]   // together more than 100 %
    [InlineData("-0.1", "0.2")]
    [InlineData("0.5", "1.2")]
    public void FleetOptions_ImpossibleOfferRates_AreRejected(string accept, string reject) =>
        Should.Throw<ArgumentException>(() => FleetOptions.Parse(["--accept-rate", accept, "--reject-rate", reject]));
}
