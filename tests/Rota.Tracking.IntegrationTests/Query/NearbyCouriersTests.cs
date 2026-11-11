using Grpc.Core;
using Rota.Contracts.Tracking.V1;
using Rota.Tracking.IntegrationTests.Support;

namespace Rota.Tracking.IntegrationTests.Query;

/// <summary>
/// <c>GetNearbyCouriers</c> (PASS 3): the right couriers, nearest first, fresh ones only, with the
/// positions fixed at known distances. Real Redis: the search and the Lua script are the thing under test.
/// </summary>
public sealed class NearbyCouriersTests(TrackingFixture fixture)
{
    private const string Dispatch = "service:dispatch";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nearby_ReturnsCouriersInsideTheRadius_NearestFirst_WithTheirDistances()
    {
        var center = TestArea.NextCenter();
        var far = await SeedAsync(fixture.Api, center, 3_000);
        var near = await SeedAsync(fixture.Api, center, 500);
        var middle = await SeedAsync(fixture.Api, center, 1_500);

        var response = await NearbyAsync(fixture.Api, center, radius: 2_000);

        // Outside the 2 km radius (far) is not there; the other two come nearest first.
        response.Couriers.Select(c => c.CourierId).ShouldBe([near.ToString("D"), middle.ToString("D")]);
        response.Couriers[0].DistanceM.ShouldBeInRange(495, 505);
        response.Couriers[1].DistanceM.ShouldBeInRange(1_485, 1_515);
        response.Couriers.Select(c => c.CourierId).ShouldNotContain(far.ToString("D"));
    }

    [Fact]
    public async Task Nearby_NobodyInRange_ReturnsAnEmptyList()
    {
        var center = TestArea.NextCenter();
        await SeedAsync(fixture.Api, center, 10_000);

        var response = await NearbyAsync(fixture.Api, center, radius: 1_000);

        response.Couriers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nearby_RespectsTheLimit_AndKeepsTheNearest()
    {
        var center = TestArea.NextCenter();
        var ids = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            ids.Add(await SeedAsync(fixture.Api, center, i * 200));
        }

        var response = await NearbyAsync(fixture.Api, center, radius: 5_000, limit: 3);

        response.Couriers.Select(c => c.CourierId).ShouldBe(ids.Take(3).Select(i => i.ToString("D")));
    }

    [Fact]
    public async Task Nearby_ACourierWhoStoppedSending_DropsOut_AtExactlyThirtySeconds_NotBefore()
    {
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, fixture.PostgresConnectionString);
        var center = TestArea.NextCenter();
        var courier = await SeedAsync(api, center, 500);

        api.Time.Advance(TimeSpan.FromSeconds(30));
        var atThirty = await NearbyAsync(api, center, radius: 2_000);
        api.Time.Advance(TimeSpan.FromSeconds(1));
        var atThirtyOne = await NearbyAsync(api, center, radius: 2_000);

        atThirty.Couriers.Select(c => c.CourierId).ShouldBe([courier.ToString("D")]);
        atThirtyOne.Couriers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nearby_MaxAgeSeconds_ChangesWhatCountsAsFresh()
    {
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, fixture.PostgresConnectionString);
        var center = TestArea.NextCenter();
        var courier = await SeedAsync(api, center, 500);
        api.Time.Advance(TimeSpan.FromSeconds(100));

        var strict = await NearbyAsync(api, center, radius: 2_000);                       // default 30 s
        var relaxed = await NearbyAsync(api, center, radius: 2_000, maxAgeSeconds: 120);

        strict.Couriers.ShouldBeEmpty();
        relaxed.Couriers.Select(c => c.CourierId).ShouldBe([courier.ToString("D")]);
    }

    [Fact]
    public async Task Nearby_StaleCouriersThatAreCloser_DoNotUseUpTheLimit()
    {
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, fixture.PostgresConnectionString);
        var center = TestArea.NextCenter();

        // Five couriers close by that then went quiet...
        for (var i = 1; i <= 5; i++)
        {
            await SeedAsync(api, center, i * 100);
        }

        api.Time.Advance(TimeSpan.FromSeconds(60));

        // ...and two a bit further that are still sending.
        var fresh1 = await SeedAsync(api, center, 1_000);
        var fresh2 = await SeedAsync(api, center, 1_500);

        // "Nearest 2, then filter" would return nothing (the two nearest are stale). The search must still find both.
        var response = await NearbyAsync(api, center, radius: 5_000, limit: 2);

        response.Couriers.Select(c => c.CourierId).ShouldBe([fresh1.ToString("D"), fresh2.ToString("D")]);
    }

    [Fact]
    public async Task Nearby_ReportsWhenEachCourierWasLastSeen_OnTheServerClock()
    {
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, fixture.PostgresConnectionString);
        var center = TestArea.NextCenter();
        await SeedAsync(api, center, 500);

        api.Time.Advance(TimeSpan.FromSeconds(10));
        var response = await NearbyAsync(api, center, radius: 2_000);

        response.Couriers.ShouldHaveSingleItem().LastSeenAt.ToDateTimeOffset().ShouldBe(Rota.TestKit.TestClock.Start);
    }

    [Theory]
    [InlineData(91d, 29d, 1000, 10, 30)]            // latitude Redis cannot hold
    [InlineData(40d, 29d, 0, 10, 30)]               // radius is required
    [InlineData(40d, 29d, 50_001, 10, 30)]          // wider than any service area
    [InlineData(40d, 29d, 1000, 101, 30)]           // limit above the cap
    [InlineData(40d, 29d, 1000, -1, 30)]
    [InlineData(40d, 29d, 1000, 10, -5)]
    [InlineData(40d, 29d, 1000, 10, 301)]           // older than Redis remembers
    public async Task Nearby_InvalidRequest_IsInvalidArgument(double lat, double lon, int radius, int limit, int maxAge)
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetNearbyCouriersAsync(
                new GetNearbyCouriersRequest { Lat = lat, Lon = lon, RadiusM = radius, Limit = limit, MaxAgeSeconds = maxAge },
                TrackingApiFactory.As(Dispatch), cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Theory]
    [InlineData("courier:00000000-0000-0000-0000-000000000001")]   // a courier must not see where the others are
    [InlineData("merchant:merchant-1")]
    [InlineData("dispatcher:dispatcher-1")]
    public async Task Nearby_ByAnyoneButAService_IsPermissionDenied(string devUser)
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetNearbyCouriersAsync(new GetNearbyCouriersRequest { Lat = 40, Lon = 29, RadiusM = 1000 }, TrackingApiFactory.As(devUser), cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Nearby_WithoutIdentity_IsUnauthenticated()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetNearbyCouriersAsync(new GetNearbyCouriersRequest { Lat = 40, Lon = 29, RadiusM = 1000 }, cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    /// <summary>Puts a courier <paramref name="metersNorth"/> metres from the centre, as the courier's own stream would.</summary>
    internal static async Task<Guid> SeedAsync(TrackingApiFactory api, (double Lat, double Lon) center, double metersNorth)
    {
        var courier = Guid.CreateVersion7();
        var (lat, lon) = TestArea.North(center, metersNorth);
        await Streams.SendAsync(api, courier, Token, Streams.Point(1, lat, lon));
        return courier;
    }

    private static async Task<GetNearbyCouriersResponse> NearbyAsync(
        TrackingApiFactory api, (double Lat, double Lon) center, int radius, int limit = 0, int maxAgeSeconds = 0)
    {
        using var channel = api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);
        return await client.GetNearbyCouriersAsync(
            new GetNearbyCouriersRequest { Lat = center.Lat, Lon = center.Lon, RadiusM = radius, Limit = limit, MaxAgeSeconds = maxAgeSeconds },
            TrackingApiFactory.As(Dispatch), cancellationToken: Token);
    }
}
