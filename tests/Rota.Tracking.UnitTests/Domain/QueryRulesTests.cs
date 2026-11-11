using Rota.Tracking.Api.Domain;

namespace Rota.Tracking.UnitTests.Domain;

public sealed class QueryRulesTests
{
    [Fact]
    public void ValidateNearby_ZeroLimitAndMaxAge_UseTheDefaults()
    {
        var query = QueryRules.ValidateNearby(40.99, 29.02, radiusMeters: 5_000, limit: 0, maxAgeSeconds: 0).Value;

        query.Limit.ShouldBe(20);
        query.MaxAge.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ValidateNearby_ExplicitValues_AreKept()
    {
        var query = QueryRules.ValidateNearby(40.99, 29.02, radiusMeters: 5_000, limit: 7, maxAgeSeconds: 90).Value;

        query.ShouldSatisfyAllConditions(
            q => q.Limit.ShouldBe(7),
            q => q.MaxAge.ShouldBe(TimeSpan.FromSeconds(90)),
            q => q.RadiusMeters.ShouldBe(5_000));
    }

    [Theory]
    [InlineData(1, 1, 300)]       // the smallest radius, the smallest limit, the largest age: all allowed
    [InlineData(50_000, 100, 1)]  // the largest radius and limit
    public void ValidateNearby_TheEdgesOfEveryRange_AreAllowed(int radius, int limit, int maxAge) =>
        QueryRules.ValidateNearby(40, 29, radius, limit, maxAge).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(0, 10, 30)]
    [InlineData(-1, 10, 30)]
    [InlineData(50_001, 10, 30)]
    [InlineData(1000, 101, 30)]
    [InlineData(1000, -1, 30)]
    [InlineData(1000, 10, -1)]
    [InlineData(1000, 10, 301)]
    public void ValidateNearby_OutsideTheRanges_IsRejected(int radius, int limit, int maxAge) =>
        QueryRules.ValidateNearby(40, 29, radius, limit, maxAge).Error.ShouldNotBeNull().Code.ShouldBe("validation-failed");

    [Theory]
    [InlineData(double.NaN, 29d)]
    [InlineData(40d, double.NaN)]
    [InlineData(86d, 29d)]
    public void ValidateNearby_ACentreRedisCannotHold_IsRejected(double lat, double lon) =>
        QueryRules.ValidateNearby(lat, lon, 1000, 10, 30).IsFailure.ShouldBeTrue();

    [Fact]
    public void ValidateCourierIds_GoodIds_AreParsedInOrder()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        QueryRules.ValidateCourierIds([a.ToString("D"), b.ToString("N")]).Value.ShouldBe([a, b]);
    }

    [Fact]
    public void ValidateCourierIds_OneBadId_RejectsTheWholeCall() =>
        QueryRules.ValidateCourierIds([Guid.CreateVersion7().ToString("D"), "nope"]).IsFailure.ShouldBeTrue();

    [Fact]
    public void ValidateCourierIds_ExactlyTheLimit_IsAllowed_OneMoreIsNot()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => Guid.CreateVersion7().ToString("D")).ToList();

        QueryRules.ValidateCourierIds(ids).IsSuccess.ShouldBeTrue();
        QueryRules.ValidateCourierIds([.. ids, Guid.CreateVersion7().ToString("D")]).IsFailure.ShouldBeTrue();
    }
}
