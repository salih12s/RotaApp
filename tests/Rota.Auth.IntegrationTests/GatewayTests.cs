extern alias GatewayApp;

using System.Globalization;
using System.Net;

namespace Rota.Auth.IntegrationTests;

/// <summary>The gateway: it checks the token first, passes good requests (and the trace) on, and the rate limit is one limit across all instances (PASS 3).</summary>
[Trait("Category", "Auth")]
public sealed class GatewayTests(AuthFixture fixture)
{
    private ServiceHost<GatewayApp::Program> NewGateway(FakeBackend backend, string ruleName, int limit, int perSeconds) =>
        new(fixture.ProductionSettings(new Dictionary<string, string>
        {
            ["ConnectionStrings:Redis"] = fixture.RedisConnection,
            ["ReverseProxy:Clusters:orders:Destinations:d1:Address"] = backend.Address,

            // First rule wins; replace the order-creation rule. A unique name = a bucket no other test shares.
            ["RateLimit:Rules:0:Name"] = ruleName,
            ["RateLimit:Rules:0:Method"] = "POST",
            ["RateLimit:Rules:0:PathPrefix"] = "/api/v1/orders",
            ["RateLimit:Rules:0:Limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["RateLimit:Rules:0:PerSeconds"] = perSeconds.ToString(CultureInfo.InvariantCulture),
            ["RateLimit:Rules:0:Key"] = "merchant",
        }));

    private static string NewRuleName() => $"t-{Guid.CreateVersion7():N}";

    [Fact]
    public async Task The_gateway_refuses_a_request_without_a_token_before_proxying_it()
    {
        await using var backend = await FakeBackend.StartAsync();
        await using var gateway = NewGateway(backend, NewRuleName(), 5, 60);
        using var client = gateway.CreateClient();
        using var body = new StringContent("{}");

        using var response = await client.PostAsync(new Uri("/api/v1/orders", UriKind.Relative), body, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        backend.LastAuthorization.ShouldBeNull("the backend must never have seen the request");
    }

    [Fact]
    public async Task The_health_endpoint_needs_no_token()
    {
        await using var backend = await FakeBackend.StartAsync();
        await using var gateway = NewGateway(backend, NewRuleName(), 5, 60);
        using var client = gateway.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_valid_token_is_proxied_with_its_authorization_and_the_trace_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backend = await FakeBackend.StartAsync();
        await using var gateway = NewGateway(backend, NewRuleName(), 5, 60);
        using var client = gateway.CreateClient();
        var token = await fixture.Tokens.MerchantAAsync();
        const string traceId = "0af7651916cd43dd8448eb211c80319c";

        using var request = Bearer.Request(HttpMethod.Get, "/api/v1/orders", token);
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");
        using var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        backend.LastAuthorization.ShouldBe($"Bearer {token}", "the service validates the token itself (defense in depth)");
        backend.LastTraceparent.ShouldNotBeNull().ShouldContain(traceId, customMessage: "the trace continues behind the gateway");
    }

    [Fact]
    public async Task Two_gateway_instances_share_one_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        const int limit = 20;
        var rule = NewRuleName();
        await using var backend = await FakeBackend.StartAsync();
        await using var first = NewGateway(backend, rule, limit, perSeconds: 60);
        await using var second = NewGateway(backend, rule, limit, perSeconds: 60);
        using var clientOne = first.CreateClient();
        using var clientTwo = second.CreateClient();
        var token = await fixture.Tokens.MerchantAAsync();

        // 2 x the limit, alternating between the instances. A request the limiter lets through reaches the backend (200); a refused one is 429.
        var refused = 0;
        for (var i = 0; i < limit * 2; i++)
        {
            using var request = Bearer.Request(HttpMethod.Post, "/api/v1/orders", token);
            using var response = await (i % 2 == 0 ? clientOne : clientTwo).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused++;
                response.Headers.RetryAfter.ShouldNotBeNull();
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }

        var passed = (limit * 2) - refused;

        // The bucket refills 20 tokens per minute: a few seconds of test time adds at most one.
        passed.ShouldBeInRange(limit, limit + 2, $"{passed} of {limit * 2} passed; per-instance limits would let {limit * 2} through");
    }

    [Fact]
    public async Task One_merchant_using_up_the_limit_does_not_slow_another()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backend = await FakeBackend.StartAsync();
        await using var gateway = NewGateway(backend, NewRuleName(), limit: 3, perSeconds: 60);
        using var client = gateway.CreateClient();
        var a = await fixture.Tokens.MerchantAAsync();
        var b = await fixture.Tokens.MerchantBAsync();

        for (var i = 0; i < 10; i++)
        {
            using var request = Bearer.Request(HttpMethod.Post, "/api/v1/orders", a);
            using var response = await client.SendAsync(request, ct);
        }

        using var asA = Bearer.Request(HttpMethod.Post, "/api/v1/orders", a);
        using var asB = Bearer.Request(HttpMethod.Post, "/api/v1/orders", b);
        using var responseA = await client.SendAsync(asA, ct);
        using var responseB = await client.SendAsync(asB, ct);

        responseA.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        responseB.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
