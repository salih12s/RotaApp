using Rota.Dispatch.Api.Domain;
using Rota.TestKit;

namespace Rota.Dispatch.UnitTests.Domain;

public sealed class CandidateRankerTests
{
    private static readonly DateTimeOffset _now = TestClock.Start;

    private static Candidate Make(int meters, CourierState state = CourierState.Available, int secondsAgo = 0, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), state, meters, _now - TimeSpan.FromSeconds(secondsAgo));

    [Fact]
    public void Rank_OrdersByDistance()
    {
        var far = Make(900);
        var near = Make(100);
        var middle = Make(500);

        CandidateRanker.Rank([far, near, middle]).ShouldBe([near, middle, far]);
    }

    [Theory]
    [InlineData(CourierState.Offline)]
    [InlineData(CourierState.Offered)]
    [InlineData(CourierState.OnDelivery)]
    public void Rank_DropsCouriersThatAreNotAvailable(CourierState state)
    {
        var busyButNearest = Make(10, state);
        var available = Make(800);

        CandidateRanker.Rank([busyButNearest, available]).ShouldBe([available]);
    }

    [Fact]
    public void Rank_OnEqualDistance_PrefersFreshestLocation()
    {
        var stale = Make(300, secondsAgo: 25);
        var fresh = Make(300, secondsAgo: 2);

        CandidateRanker.Rank([stale, fresh]).ShouldBe([fresh, stale]);
    }

    [Fact]
    public void Rank_OnFullTie_IsDeterministicByCourierId()
    {
        // Same input in any order → same output; otherwise retries could offer to different couriers.
        var a = Make(300, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var b = Make(300, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));

        CandidateRanker.Rank([b, a]).ShouldBe([a, b]);
        CandidateRanker.Rank([a, b]).ShouldBe([a, b]);
    }

    [Fact]
    public void Rank_NoCandidates_ReturnsEmpty() =>
        CandidateRanker.Rank([]).ShouldBeEmpty();
}

public sealed class EtaCalculatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]          // 0.18 s rounds up, never down to "0 s away"
    [InlineData(1_000, 180)]    // 20 km/h = 5.56 m/s → 1 km in 3 min
    [InlineData(15_000, 2_700)] // the service-area limit: 45 min
    public void EstimateSeconds_AtDefaultSpeed_RoundsUp(int meters, int expectedSeconds) =>
        new EtaCalculator().EstimateSeconds(meters).ShouldBe(expectedSeconds);

    [Fact]
    public void EstimateSeconds_UsesConfiguredSpeed() =>
        new EtaCalculator(averageSpeedKmh: 36).EstimateSeconds(1_000).ShouldBe(100); // 10 m/s

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_WithInvalidSpeed_Throws(double speed) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new EtaCalculator(speed));

    [Fact]
    public void EstimateSeconds_NegativeDistance_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new EtaCalculator().EstimateSeconds(-1));
}
