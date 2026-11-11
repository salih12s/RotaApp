using System.Net;

namespace Rota.Tracking.IntegrationTests;

/// <summary>Liveness says "the process is up"; readiness says "Redis answers".</summary>
public sealed class HealthTests(TrackingFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ready_WhenRedisAnswers_Returns200()
    {
        using var client = fixture.Api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_WhenRedisIsUnreachable_Returns503_ButLiveStaysHealthy()
    {
        // Nothing listens on port 1; short timeouts keep the test fast. The service still starts.
        await using var api = new TrackingApiFactory("localhost:1,connectTimeout=300,syncTimeout=300,asyncTimeout=300", fixture.PostgresConnectionString);
        using var client = api.CreateClient();

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Token);
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Token);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
