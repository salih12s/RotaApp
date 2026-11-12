using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rota.Sim.Common;

/// <summary>
/// How a simulator proves who it is. Without <c>--auth</c> it sends the <c>X-Dev-User</c> header (DevAuth, Development only); with
/// <c>--auth http://localhost:8180/realms/rota</c> it gets real tokens from the dev realm (deploy/keycloak/realm-rota.json):
/// merchants by client credentials, couriers by password grant (the dev realm allows it so simulators need no browser; real
/// phones use Authorization Code + PKCE), internal calls as <c>svc-internal</c>. Tokens are renewed shortly before they expire.
/// </summary>
public sealed class SimCredentials : IDisposable
{
    /// <summary>The dev realm holds this many simulated merchants (<c>merchant-sim-0..4</c>) and couriers (<c>sim-courier-00..49</c>).</summary>
    public const int MaxMerchants = 5;

    public const int MaxCouriers = 50;

    private static readonly TimeSpan _renewBefore = TimeSpan.FromSeconds(30);

    private readonly Uri? _tokenEndpoint;
    private readonly TimeProvider _time;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<string, TokenSource> _sources = new(StringComparer.Ordinal);

    public SimCredentials(Uri? authority, TimeProvider time)
    {
        _time = time;
        _tokenEndpoint = authority is null ? null : new Uri($"{authority.ToString().TrimEnd('/')}/protocol/openid-connect/token");
    }

    public bool Enabled => _tokenEndpoint is not null;

    /// <summary>The courier's id in the dev realm: the same for the same index, so couriers survive restarts.</summary>
    public static Guid CourierId(int index) => new($"c1000000-0000-4000-8000-{index.ToString("D12", CultureInfo.InvariantCulture)}");

    /// <summary>Bearer token for an identity written as <c>role:id</c> (<c>merchant:merchant-sim-2</c>, <c>courier:&lt;guid&gt;</c>, <c>service:x</c>); null when not using Keycloak.</summary>
    public async Task<string?> BearerAsync(string devUser, CancellationToken cancellationToken = default)
    {
        if (_tokenEndpoint is null)
        {
            return null;
        }

        var separator = devUser.IndexOf(':', StringComparison.Ordinal);
        var (role, id) = (devUser[..separator], devUser[(separator + 1)..]);
        var source = _sources.GetOrAdd(devUser, _ => role switch
        {
            "merchant" => new TokenSource(this, new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = id, ["client_secret"] = "dev-sim-merchant-secret",
            }),
            "courier" => new TokenSource(this, new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "rota-app", ["password"] = "dev",
                ["username"] = $"sim-courier-{CourierIndex(id).ToString("D2", CultureInfo.InvariantCulture)}",
            }),
            "service" => new TokenSource(this, new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = "svc-internal", ["client_secret"] = "dev-svc-internal-secret",
            }),
            _ => throw new ArgumentException($"Unknown identity '{devUser}'.", nameof(devUser)),
        });

        return await source.GetAsync(cancellationToken);
    }

    /// <summary>Marks the request as coming from <paramref name="devUser"/>: a bearer token, or the dev header.</summary>
    public async Task SignAsync(HttpRequestMessage request, string devUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await BearerAsync(devUser, cancellationToken);
        if (token is null)
        {
            request.Headers.Add("X-Dev-User", devUser);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _http.Dispose();
    }

    // The id is "c1000000-0000-4000-8000-0000000000NN": the courier's index is the last group.
    private static int CourierIndex(string courierId) =>
        int.Parse(courierId[^12..], CultureInfo.InvariantCulture);

    private sealed class TokenSource(SimCredentials owner, Dictionary<string, string> form) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _token;

        public void Dispose() => _lock.Dispose();
        private DateTimeOffset _renewAt;

        public async Task<string> GetAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_token is null || owner._time.GetUtcNow() >= _renewAt)
                {
                    using var response = await owner._http.PostAsync(owner._tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    _token = json.GetProperty("access_token").GetString()!;
                    _renewAt = owner._time.GetUtcNow() + TimeSpan.FromSeconds(json.GetProperty("expires_in").GetInt32()) - _renewBefore;
                }

                return _token;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
