using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Rota.Contracts.Tracking.V1;
using Rota.TestKit;

namespace Rota.Tracking.IntegrationTests.Ingest;

/// <summary>Who may open the position stream: the courier identity comes from the token, never from the messages.</summary>
public sealed class IngestAccessTests(TrackingFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamLocations_AsCourier_ReceivesEveryPoint()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationIngest.CourierLocationIngestClient(channel);

        var result = await SendAsync(client, TrackingApiFactory.As(DevUsers.Courier(Guid.CreateVersion7())), pointCount: 3);

        result.Received.ShouldBe(3);
    }

    [Fact]
    public async Task StreamLocations_WithoutIdentity_IsUnauthenticated()
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationIngest.CourierLocationIngestClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(() => SendAsync(client, headers: null, pointCount: 1));

        failure.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData("merchant:merchant-1")]
    [InlineData("dispatcher:dispatcher-1")]
    [InlineData("service:dispatch")]
    public async Task StreamLocations_AsAnotherRole_IsPermissionDenied(string devUser)
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationIngest.CourierLocationIngestClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(() => SendAsync(client, TrackingApiFactory.As(devUser), pointCount: 1));

        failure.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    internal static async Task<StreamLocationsResult> SendAsync(
        CourierLocationIngest.CourierLocationIngestClient client, Metadata? headers, int pointCount)
    {
        using var call = client.StreamLocations(headers, cancellationToken: Token);
        for (var i = 1; i <= pointCount; i++)
        {
            await call.RequestStream.WriteAsync(
                new LocationPoint { Seq = i, Lat = 40.99, Lon = 29.02, RecordedAt = Timestamp.FromDateTimeOffset(TestClock.Start) }, Token);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }
}
