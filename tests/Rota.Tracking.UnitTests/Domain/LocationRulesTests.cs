using Rota.Tracking.Api.Domain;
using Rota.TestKit;

namespace Rota.Tracking.UnitTests.Domain;

public sealed class LocationRulesTests
{
    private static readonly DateTimeOffset _now = TestClock.Start;
    private static readonly TimeSpan _maxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan _skew = TimeSpan.FromMinutes(5);

    private static Rota.BuildingBlocks.Primitives.Result Check(
        long seq = 1, double lat = 40.99, double lon = 29.02, float accuracy = 5, DateTimeOffset? recordedAt = null, bool noTime = false) =>
        LocationRules.Validate(seq, lat, lon, accuracy, noTime ? null : recordedAt ?? _now, _now, _maxAge, _skew);

    [Fact]
    public void Validate_NormalPoint_Succeeds() => Check().IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(90.0)]            // a real latitude, but Redis GEO refuses it
    [InlineData(85.06)]
    [InlineData(-85.06)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_LatitudeOutsideTheRedisRange_IsRejected(double lat) =>
        Check(lat: lat).Error.ShouldNotBeNull().Code.ShouldBe("validation-failed");

    [Theory]
    [InlineData(180.1)]
    [InlineData(-181)]
    [InlineData(double.NaN)]
    public void Validate_LongitudeOutOfRange_IsRejected(double lon) =>
        Check(lon: lon).Error.ShouldNotBeNull().Code.ShouldBe("validation-failed");

    [Theory]
    [InlineData(85.05112878)]     // the edge itself is allowed
    [InlineData(-85.05112878)]
    public void Validate_LatitudeAtTheEdge_Succeeds(double lat) => Check(lat: lat).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void Validate_NonPositiveSeq_IsRejected(long seq) => Check(seq: seq).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Validate_BadAccuracy_IsRejected(float accuracy) => Check(accuracy: accuracy).IsFailure.ShouldBeTrue();

    [Fact]
    public void Validate_MissingRecordedAt_IsRejected() => Check(noTime: true).Error.ShouldNotBeNull().Message.ShouldContain("recorded_at");

    [Fact]
    public void Validate_PointExactlyAtTheMaxAge_IsAccepted_OneSecondOlderIsNot()
    {
        Check(recordedAt: _now - _maxAge).IsSuccess.ShouldBeTrue();
        Check(recordedAt: _now - _maxAge - TimeSpan.FromSeconds(1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Validate_FutureTime_IsAcceptedWithinTheSkew_RejectedBeyondIt()
    {
        Check(recordedAt: _now + _skew).IsSuccess.ShouldBeTrue();
        Check(recordedAt: _now + _skew + TimeSpan.FromSeconds(1)).IsFailure.ShouldBeTrue();
    }
}
