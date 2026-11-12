extern alias OrdersApi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// What a token must be to count (authority, signature, audience) and that a header cannot stand in for one outside Development.
/// Also PASS 2: asking a service directly, past the gateway, with no token is refused.
/// </summary>
[Trait("Category", "Auth")]
public sealed class TokenValidationTests(AuthFixture fixture)
{
    private static readonly object NewOrder = new
    {
        merchantOrderRef = "PZ-auth-1",
        pickup = new { lat = 40.9903, lon = 29.0290, address = "Kadıköy Şube, Moda Cd. 10" },
        dropoff = new { lat = 40.9850, lon = 29.0400, address = "Caferağa Mh. Moda" },
        customer = new { name = "Ayşe Y.", phone = "+905551112233" },
    };

    [Fact]
    public async Task Bypassing_the_gateway_without_a_token_is_401_on_every_service()
    {
        var ct = TestContext.Current.CancellationToken;
        using var orders = fixture.Orders.CreateClient();
        using var dispatch = fixture.Dispatch.CreateClient();
        using var notifications = fixture.Notifications.CreateClient();

        foreach (var (client, path) in new[]
        {
            (orders, "/api/v1/orders"),
            (dispatch, "/api/v1/couriers"),
            (notifications, "/api/v1/webhook-deliveries"),
        })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task A_merchant_creates_an_order_and_sees_it_but_another_merchant_gets_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await fixture.Tokens.MerchantAAsync();
        var b = await fixture.Tokens.MerchantBAsync();
        using var client = fixture.Orders.CreateClient();

        using var create = Bearer.Request(HttpMethod.Post, "/api/v1/orders", a, JsonSerializer.Serialize(NewOrder));
        create.Headers.Add("Idempotency-Key", $"auth-{Guid.CreateVersion7():N}");
        using var created = await client.SendAsync(create, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        using var own = await client.SendAsync(Bearer.Request(HttpMethod.Get, $"/api/v1/orders/{id}", a), ct);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Right role, wrong owner: 404, not 403 (no "it exists but is not yours" leak).
        using var foreign = await client.SendAsync(Bearer.Request(HttpMethod.Get, $"/api/v1/orders/{id}", b), ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A courier who is not assigned to it: also 404.
        var courier = await fixture.Tokens.CourierAsync();
        using var stranger = await client.SendAsync(Bearer.Request(HttpMethod.Get, $"/api/v1/orders/{id}", courier), ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_valid_token_issued_for_another_API_is_401()
    {
        using var client = fixture.Orders.CreateClient();
        var token = await fixture.Tokens.OtherApiAsync();

        using var response = await client.SendAsync(
            Bearer.Request(HttpMethod.Get, "/api/v1/orders", token), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_with_a_tampered_payload_is_401()
    {
        using var client = fixture.Orders.CreateClient();
        var token = await fixture.Tokens.CourierAsync();
        var parts = token.Split('.');

        // Promote the courier to a merchant in the payload; the signature no longer matches.
        var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1])).Replace("\"courier\"", "\"merchant\"", StringComparison.Ordinal);
        var forged = $"{parts[0]}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";

        using var response = await client.SendAsync(
            Bearer.Request(HttpMethod.Get, "/api/v1/orders", forged), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_dev_header_means_nothing_outside_Development()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Orders.CreateClient();

        // The header alone: anonymous, so 401.
        using var headerOnly = Bearer.Request(HttpMethod.Get, "/api/v1/orders");
        headerOnly.Headers.Add("X-Dev-User", "merchant:merchant-demo-a");
        using var headerOnlyResponse = await client.SendAsync(headerOnly, ct);
        headerOnlyResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A courier's token plus a header claiming to be a merchant: the token wins, the courier is refused (403).
        var courier = await fixture.Tokens.CourierAsync();
        using var both = Bearer.Request(HttpMethod.Get, "/api/v1/orders", courier);
        both.Headers.Add("X-Dev-User", "merchant:merchant-demo-a");
        using var bothResponse = await client.SendAsync(both, ct);
        bothResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static byte[] Base64UrlDecode(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
