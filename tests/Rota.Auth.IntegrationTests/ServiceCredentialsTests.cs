extern alias TrackingApi;

using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Tracking.V1;
using Microsoft.Extensions.Time.Testing;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// How Dispatch and Ops API prove who they are to Tracking (docs/06 §1): a client-credentials token fetched from the real Keycloak, reused until shortly
/// before it expires, and never a silent downgrade to the dev header outside Development. The unit under test is <see cref="ServiceCredentials"/> itself.
/// </summary>
[Trait("Category", "Auth")]
public sealed class ServiceCredentialsTests(AuthFixture fixture)
{
    private sealed class CountingFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Requests++;
            return inner.CreateClient(name);
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private (ServiceCredentials Credentials, CountingFactory Http, FakeTimeProvider Time) Create(
        string? clientId, string? secret, string environment = "Production")
    {
        var http = new CountingFactory(new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var credentials = new ServiceCredentials(
            http,
            Options.Create(new RotaAuthOptions { Authority = fixture.Authority, RequireHttpsMetadata = false }),
            Options.Create(new ServiceCredentialsOptions { ClientId = clientId, ClientSecret = secret }),
            new Environment(environment),
            time);
        return (credentials, http, time);
    }

    [Fact]
    public async Task A_service_token_from_Keycloak_is_accepted_by_Tracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (credentials, _, _) = Create("svc-internal", "dev-svc-internal-secret");
        using var _ = credentials;

        var token = await credentials.GetBearerAsync(ct);

        token.ShouldNotBeNullOrEmpty();
        // Not just "looks like a token": Tracking, started as in production (real JWT validation), lets it read as a service.
        var client = fixture.Tracking.CreateDefaultClient(new TrackingGrpcAuthTests.Http2ResponseHandler());
        using var channel = GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });
        var query = new CourierLocationQuery.CourierLocationQueryClient(channel);
        var response = await query.GetLastLocationsAsync(new GetLastLocationsRequest(), new Metadata { { "authorization", $"Bearer {token}" } }, cancellationToken: ct);
        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_token_is_reused_until_shortly_before_it_expires_then_renewed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (credentials, http, time) = Create("svc-internal", "dev-svc-internal-secret");
        using var _ = credentials;

        var first = await credentials.GetBearerAsync(ct);
        var second = await credentials.GetBearerAsync(ct);

        second.ShouldBe(first);
        http.Requests.ShouldBe(1, "a token per call would hammer Keycloak");

        // Keycloak's default access token lifetime is minutes; an hour later it is certainly past the renewal point.
        time.Advance(TimeSpan.FromHours(1));
        var renewed = await credentials.GetBearerAsync(ct);

        http.Requests.ShouldBe(2);
        renewed.ShouldNotBe(first);
    }

    [Fact]
    public async Task A_wrong_secret_fails_loudly()
    {
        var (credentials, _, _) = Create("svc-internal", "not-the-secret");
        using var _ = credentials;

        await Should.ThrowAsync<HttpRequestException>(async () => await credentials.GetBearerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Outside_Development_a_missing_client_is_an_error_not_a_silent_dev_header()
    {
        var (credentials, _, _) = Create(clientId: null, secret: null, environment: "Production");
        using var _ = credentials;

        await Should.ThrowAsync<InvalidOperationException>(async () => await credentials.GetBearerAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task In_Development_a_missing_client_means_use_the_dev_header()
    {
        var (credentials, _, _) = Create(clientId: null, secret: null, environment: "Development");
        using var _ = credentials;

        (await credentials.GetBearerAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
