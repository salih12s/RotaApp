extern alias TrackingApi;

using System.Net;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Rota.Contracts.Tracking.V1;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// Tracking speaks gRPC only; the bearer token travels as call metadata (<c>authorization</c>). Same rules as REST (docs/06 §2):
/// courier-only location ingest, service-only internal reads. Asked directly, past the gateway (PASS 2).
/// </summary>
[Trait("Category", "Auth")]
public sealed class TrackingGrpcAuthTests(AuthFixture fixture)
{
    private static Metadata Bearer(string? token) =>
        token is null ? [] : new Metadata { { "authorization", $"Bearer {token}" } };

    private GrpcChannel Channel()
    {
        var client = fixture.Tracking.CreateDefaultClient(new Http2ResponseHandler());
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });
    }

    private static readonly GetNearbyCouriersRequest Nearby = new() { Lat = 41.01, Lon = 28.98, RadiusM = 5_000, Limit = 10 };

    [Fact]
    public async Task An_internal_read_without_a_token_is_Unauthenticated()
    {
        using var channel = Channel();
        var query = new CourierLocationQuery.CourierLocationQueryClient(channel);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await query.GetNearbyCouriersAsync(Nearby, Bearer(null), cancellationToken: TestContext.Current.CancellationToken));

        failure.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData("courier")]
    [InlineData("merchant")]
    [InlineData("dispatcher")]
    public async Task An_internal_read_by_anyone_but_a_service_is_PermissionDenied(string role)
    {
        using var channel = Channel();
        var query = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var token = await fixture.Tokens.ForRoleAsync(role);

        var failure = await Should.ThrowAsync<RpcException>(async () =>
            await query.GetNearbyCouriersAsync(Nearby, Bearer(token), cancellationToken: TestContext.Current.CancellationToken));

        failure.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task An_internal_read_with_a_service_token_succeeds()
    {
        using var channel = Channel();
        var query = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var token = await fixture.Tokens.ServiceAsync();

        var response = await query.GetNearbyCouriersAsync(Nearby, Bearer(token), cancellationToken: TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task Location_ingest_needs_a_courier_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = Channel();
        var ingest = new CourierLocationIngest.CourierLocationIngestClient(channel);

        async Task<StreamLocationsResult> SendOnePointAsync(string? token)
        {
            using var call = ingest.StreamLocations(Bearer(token), cancellationToken: ct);
            await call.RequestStream.WriteAsync(
                new LocationPoint { Seq = 1, Lat = 41.01, Lon = 28.98, AccuracyM = 5, RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) }, ct);
            await call.RequestStream.CompleteAsync();
            return await call.ResponseAsync;
        }

        (await Should.ThrowAsync<RpcException>(() => SendOnePointAsync(null))).StatusCode.ShouldBe(StatusCode.Unauthenticated);
        (await Should.ThrowAsync<RpcException>(async () => await SendOnePointAsync(await fixture.Tokens.MerchantAAsync())))
            .StatusCode.ShouldBe(StatusCode.PermissionDenied);

        var result = await SendOnePointAsync(await fixture.Tokens.CourierAsync());
        result.Received.ShouldBe(1);
    }

    /// <summary>The in-memory server answers with HTTP/1.1 framing; gRPC clients insist on HTTP/2, so the response version is relabelled.</summary>
    internal sealed class Http2ResponseHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Version = HttpVersion.Version20;
            return response;
        }
    }
}
