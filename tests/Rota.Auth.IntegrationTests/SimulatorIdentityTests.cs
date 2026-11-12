using System.Net;
using System.Text;
using System.Text.Json;
using Rota.Sim.Common;

namespace Rota.Auth.IntegrationTests;

/// <summary>The simulators' identities (merchant-sim-0..4, sim-courier-00..49, svc-internal) exist in the realm and carry the right claims.</summary>
[Trait("Category", "Auth")]
public sealed class SimulatorIdentityTests(AuthFixture fixture)
{
    private static JsonElement Claims(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(
            Convert.FromBase64String(jwt.Split('.')[1].Replace('-', '+').Replace('_', '/').PadRight(((jwt.Split('.')[1].Length + 3) / 4) * 4, '='))));

    [Theory]
    [InlineData(0)]
    [InlineData(SimCredentials.MaxMerchants - 1)]
    public async Task A_simulated_merchant_gets_a_token_with_its_own_merchant_id(int index)
    {
        using var credentials = new SimCredentials(new Uri(fixture.Authority), TimeProvider.System);

        var token = await credentials.BearerAsync($"merchant:merchant-sim-{index}", TestContext.Current.CancellationToken);

        var claims = Claims(token!);
        claims.GetProperty("merchant_id").GetString().ShouldBe($"merchant-sim-{index}");
        claims.GetProperty("role").EnumerateArray().Select(r => r.GetString()).ShouldBe(["merchant"]);
        claims.GetProperty("aud").GetString().ShouldBe("rota-api");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SimCredentials.MaxCouriers - 1)]
    public async Task A_simulated_courier_gets_a_token_whose_sub_is_its_courier_id(int index)
    {
        using var credentials = new SimCredentials(new Uri(fixture.Authority), TimeProvider.System);

        var token = await credentials.BearerAsync($"courier:{SimCredentials.CourierId(index):D}", TestContext.Current.CancellationToken);

        var claims = Claims(token!);
        claims.GetProperty("sub").GetString().ShouldBe(SimCredentials.CourierId(index).ToString("D"));
        claims.GetProperty("role").EnumerateArray().Select(r => r.GetString()).ShouldBe(["courier"]);
    }

    [Fact]
    public async Task The_simulators_internal_calls_use_a_service_token()
    {
        using var credentials = new SimCredentials(new Uri(fixture.Authority), TimeProvider.System);

        var token = await credentials.BearerAsync("service:courier-fleet-sim", TestContext.Current.CancellationToken);

        Claims(token!).GetProperty("role").EnumerateArray().Select(r => r.GetString()).ShouldBe(["service"]);
    }

    [Fact]
    public async Task A_signed_request_is_accepted_by_the_real_Orders_service()
    {
        var ct = TestContext.Current.CancellationToken;
        using var credentials = new SimCredentials(new Uri(fixture.Authority), TimeProvider.System);
        using var client = fixture.Orders.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders");
        await credentials.SignAsync(request, "merchant:merchant-sim-3", ct);

        using var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_an_authority_the_simulator_falls_back_to_the_dev_header()
    {
        using var credentials = new SimCredentials(null, TimeProvider.System);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");

        await credentials.SignAsync(request, "merchant:merchant-sim-0", TestContext.Current.CancellationToken);

        request.Headers.GetValues("X-Dev-User").ShouldBe(["merchant:merchant-sim-0"]);
        request.Headers.Authorization.ShouldBeNull();
    }
}
