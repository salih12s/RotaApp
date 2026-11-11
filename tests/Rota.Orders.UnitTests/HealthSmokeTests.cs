using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Rota.TestKit.Orders;

namespace Rota.Orders.UnitTests;

public sealed class HealthSmokeTests(HealthSmokeTests.Factory factory)
    : IClassFixture<HealthSmokeTests.Factory>
{
    /// <summary>Startup validates the keys; no database is touched by these endpoints.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in OrdersTestSettings.Values("Host=unused.invalid;Database=unused"))
            {
                builder.UseSetting(key, value);
            }
        }
    }

    [Fact]
    public async Task Live_endpoint_returns_200_and_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Unknown_route_returns_404_as_problem_details()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task OpenApi_document_lists_the_order_endpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var paths = document.RootElement.GetProperty("paths");
        paths.TryGetProperty("/api/v1/orders", out _).ShouldBeTrue();
        paths.TryGetProperty("/api/v1/orders/{id}", out _).ShouldBeTrue();
        paths.TryGetProperty("/api/v1/orders/{id}/cancel", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Tracking_jwks_is_anonymous_and_has_no_private_key_member()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/.well-known/tracking-jwks", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var key = document.RootElement.GetProperty("keys").EnumerateArray().ShouldHaveSingleItem();
        key.GetProperty("kty").GetString().ShouldBe("EC");
        key.GetProperty("alg").GetString().ShouldBe("ES256");
        key.TryGetProperty("d", out _).ShouldBeFalse(); // "d" is the EC private key
    }
}
