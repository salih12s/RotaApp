using Rota.Tracking.Api.Domain;

namespace Rota.Tracking.UnitTests.Domain;

public sealed class PartitionPlanTests
{
    private static readonly TimeSpan _retention = TimeSpan.FromDays(30);

    [Fact]
    public void RequiredDays_AreYesterdayTodayAndTheNextThree()
    {
        var days = PartitionPlan.RequiredDays(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero), daysAhead: 3);

        days.ShouldBe([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14)]);
    }

    [Fact]
    public void RequiredDays_UseTheUtcDate_NotTheLocalOne()
    {
        // 22:30 UTC on the 11th is already the 12th in Istanbul (UTC+3); partitions follow UTC.
        var days = PartitionPlan.RequiredDays(new DateTimeOffset(2026, 11, 11, 22, 30, 0, TimeSpan.Zero), daysAhead: 0);

        days.ShouldBe([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11)]);
    }

    [Fact]
    public void RequiredDays_CoverTheWholeAcceptedWindow_AtAnyMomentOfTheDay()
    {
        // The accepted window starts 24 h back: that moment must always fall on a day that has a partition.
        foreach (var hour in new[] { 0, 1, 12, 23 })
        {
            var now = new DateTimeOffset(2026, 11, 11, hour, 59, 0, TimeSpan.Zero);
            var oldestAccepted = DateOnly.FromDateTime((now - TimeSpan.FromHours(24)).UtcDateTime);

            PartitionPlan.RequiredDays(now, daysAhead: 3).ShouldContain(oldestAccepted, $"at {hour}:59");
        }
    }

    [Fact]
    public void IsExpired_OnlyWhenTheWholeDayIsOlderThanTheRetention()
    {
        var now = new DateTimeOffset(2026, 12, 20, 10, 0, 0, TimeSpan.Zero);   // cutoff: 20 Nov 10:00

        PartitionPlan.IsExpired(new DateOnly(2026, 11, 19), now, _retention).ShouldBeTrue();    // ended 20 Nov 00:00
        PartitionPlan.IsExpired(new DateOnly(2026, 11, 20), now, _retention).ShouldBeFalse();   // ends 21 Nov, still holds data newer than the cutoff
        PartitionPlan.IsExpired(new DateOnly(2026, 12, 20), now, _retention).ShouldBeFalse();
    }

    [Fact]
    public void IsExpired_BoundaryIsInclusive_AtExactlyThirtyDays()
    {
        var now = new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);    // cutoff: 21 Nov 00:00 = end of 20 Nov

        PartitionPlan.IsExpired(new DateOnly(2026, 11, 20), now, _retention).ShouldBeTrue();
        PartitionPlan.IsExpired(new DateOnly(2026, 11, 21), now, _retention).ShouldBeFalse();
    }

    [Fact]
    public void NameFor_AndTryParseDay_RoundTrip()
    {
        var day = new DateOnly(2026, 1, 5);

        var name = PartitionPlan.NameFor(day);

        name.ShouldBe("location_points_20260105");
        PartitionPlan.TryParseDay(name, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(day);
    }

    [Theory]
    [InlineData("location_points_staging")]
    [InlineData("location_points_2026")]
    [InlineData("location_points_20261301")]   // month 13
    [InlineData("other_20261111")]
    public void TryParseDay_NamesThatAreNotDailyPartitions_AreIgnored(string name) =>
        PartitionPlan.TryParseDay(name, out _).ShouldBeFalse();

    [Fact]
    public void Bounds_AreUtcMidnightToMidnight()
    {
        var (from, to) = PartitionPlan.Bounds(new DateOnly(2026, 11, 11));

        from.ShouldBe("2026-11-11 00:00:00+00");
        to.ShouldBe("2026-11-12 00:00:00+00");
    }
}
