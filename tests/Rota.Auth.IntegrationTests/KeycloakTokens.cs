using System.Net.Http.Json;
using System.Text.Json;

namespace Rota.Auth.IntegrationTests;

/// <summary>The actors of docs/06 §1, as real access tokens from the seed realm (deploy/keycloak/realm-rota.json).</summary>
public sealed class KeycloakTokens(string authority) : IDisposable
{
    public const string MerchantA = "merchant-demo-a";
    public const string MerchantB = "merchant-demo-b";

    private readonly HttpClient _http = new();

    private readonly Dictionary<string, Task<string>> _cache = [];

    public void Dispose() => _http.Dispose();

    public static readonly Guid Courier1Id = Guid.Parse("c0000000-0000-4000-8000-000000000001");

    public Task<string> MerchantAAsync() => ClientCredentialsAsync(MerchantA, "dev-merchant-a-secret");

    public Task<string> MerchantBAsync() => ClientCredentialsAsync(MerchantB, "dev-merchant-b-secret");

    public Task<string> ServiceAsync() => ClientCredentialsAsync("svc-internal", "dev-svc-internal-secret");

    /// <summary>A valid, signed token that was issued for a different API: no <c>rota-api</c> audience.</summary>
    public Task<string> OtherApiAsync() => ClientCredentialsAsync("other-api-client", "dev-other-api-secret");

    public Task<string> CourierAsync() => PasswordAsync("courier-1");

    public Task<string> DispatcherAsync() => PasswordAsync("dispatcher-1");

    /// <summary>Token of the named role: <c>merchant</c>, <c>courier</c>, <c>dispatcher</c>, <c>service</c>; <c>none</c> gives null.</summary>
    public async Task<string?> ForRoleAsync(string role) => role switch
    {
        "none" => null,
        "merchant" => await MerchantAAsync(),
        "courier" => await CourierAsync(),
        "dispatcher" => await DispatcherAsync(),
        "service" => await ServiceAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private Task<string> ClientCredentialsAsync(string clientId, string secret) =>
        CachedAsync(clientId, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        });

    private Task<string> PasswordAsync(string user) =>
        CachedAsync(user, new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "rota-app",
            ["username"] = user,
            ["password"] = "dev",
        });

    // Tokens live 5 minutes; a test run is shorter, so one token per identity is enough.
    private Task<string> CachedAsync(string key, Dictionary<string, string> form)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out var task))
            {
                task = FetchAsync(form);
                _cache[key] = task;
            }

            return task;
        }
    }

    private async Task<string> FetchAsync(Dictionary<string, string> form)
    {
        using var response = await _http.PostAsync(new Uri($"{authority}/protocol/openid-connect/token"), new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }
}
