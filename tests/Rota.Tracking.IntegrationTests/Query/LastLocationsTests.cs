using Grpc.Core;
using Rota.Contracts.Tracking.V1;
using Rota.Tracking.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Tracking.IntegrationTests.Query;

/// <summary><c>GetLastLocations</c>: the batch read the Ops API uses, one call for many couriers.</summary>
public sealed class LastLocationsTests(TrackingFixture fixture)
{
    private const string OpsApi = "service:ops-api";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LastLocations_ReturnsTheCurrentPosition_WithTheDeviceTime_NotTheServerTime()
    {
        var courier = Guid.CreateVersion7();
        var recordedTwoHoursAgo = TestClock.Start.AddHours(-2);   // buffered while offline, delivered now
        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(1, 40.990, 29.020, recordedAt: recordedTwoHoursAgo));

        var response = await LastAsync(courier);

        var location = response.Locations.ShouldHaveSingleItem();
        location.CourierId.ShouldBe(courier.ToString("D"));
        location.Lat.ShouldBe(40.990, 0.0001);
        location.Lon.ShouldBe(29.020, 0.0001);
        location.RecordedAt.ToDateTimeOffset().ShouldBe(recordedTwoHoursAgo);
    }

    [Fact]
    public async Task LastLocations_SkipsCouriersNobodyHasHeardFrom_AndAnswersForTheRest()
    {
        var known = Guid.CreateVersion7();
        var unknown = Guid.CreateVersion7();
        await Streams.SendAsync(fixture.Api, known, Token, Streams.Point(1));

        var response = await LastAsync(unknown, known);

        response.Locations.Select(l => l.CourierId).ShouldBe([known.ToString("D")]);
    }

    [Fact]
    public async Task LastLocations_AnOlderPointThatArrivedLate_DoesNotChangeTheAnswer()
    {
        var courier = Guid.CreateVersion7();
        var t3 = TestClock.Start.AddMinutes(-1);
        var t2 = TestClock.Start.AddMinutes(-2);
        await Streams.SendAsync(
            fixture.Api, courier, Token, Streams.Point(1, 40.990, recordedAt: TestClock.Start.AddMinutes(-3)), Streams.Point(3, 41.000, recordedAt: t3), Streams.Point(2, 40.995, recordedAt: t2));

        var location = (await LastAsync(courier)).Locations.ShouldHaveSingleItem();

        location.Lat.ShouldBe(41.000, 0.0001);
        location.RecordedAt.ToDateTimeOffset().ShouldBe(t3, "the time must belong to the position it is shown with (seq 3), not to the late seq 2");
    }

    [Fact]
    public async Task LastLocations_NoIds_ReturnsAnEmptyList() => (await LastAsync()).Locations.ShouldBeEmpty();

    [Fact]
    public async Task LastLocations_ABadId_IsInvalidArgument()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var request = new GetLastLocationsRequest();
        request.CourierIds.Add("not-a-guid");

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetLastLocationsAsync(request, TrackingApiFactory.As(OpsApi), cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task LastLocations_MoreThanAThousandIds_IsInvalidArgument()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var request = new GetLastLocationsRequest();
        request.CourierIds.AddRange(Enumerable.Range(0, 1_001).Select(_ => Guid.CreateVersion7().ToString("D")));

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetLastLocationsAsync(request, TrackingApiFactory.As(OpsApi), cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task LastLocations_ByACourier_IsPermissionDenied()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetLastLocationsAsync(new GetLastLocationsRequest(), TrackingApiFactory.As(DevUsers.Courier(Guid.CreateVersion7())), cancellationToken: Token));

        failure.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    private async Task<GetLastLocationsResponse> LastAsync(params Guid[] couriers)
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var request = new GetLastLocationsRequest();
        request.CourierIds.AddRange(couriers.Select(c => c.ToString("D")));
        return await client.GetLastLocationsAsync(request, TrackingApiFactory.As(OpsApi), cancellationToken: Token);
    }
}
