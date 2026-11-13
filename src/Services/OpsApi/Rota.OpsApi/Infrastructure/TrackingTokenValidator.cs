using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Error = Rota.BuildingBlocks.Primitives.Error;
using Result = Rota.BuildingBlocks.Primitives.Result<Rota.OpsApi.Infrastructure.TrackingGrant>;

namespace Rota.OpsApi.Infrastructure;

/// <summary>What a valid tracking token allows: watching one order until <see cref="ExpiresAt"/>.</summary>
public sealed record TrackingGrant(Guid OrderId, DateTimeOffset ExpiresAt);

/// <summary>
/// Checks the customer's tracking token (docs/06 §1, ADR-007, invariant I-9). Orders signs it with its private ES256 key; this class only has the public
/// key, fetched from <c>/.well-known/tracking-jwks</c>, so a stolen Ops API still cannot mint tokens. A token is accepted only if its signature, issuer,
/// audience and lifetime are right <b>and</b> it names an order. Whether that order may be watched right now (status) is the caller's second check.
/// </summary>
public sealed class TrackingTokenValidator(IHttpClientFactory httpFactory, IOptions<OpsOptions> options, TimeProvider time) : IDisposable
{
    /// <summary>The named HTTP client used to reach Orders (tests replace its handler).</summary>
    public const string HttpClientName = nameof(TrackingTokenValidator);

    public const string Issuer = "rota-orders";
    public const string Audience = "rota-tracking";
    public const string OrderIdClaim = "order_id";
    public const string JwksPath = "/.well-known/tracking-jwks";

    public static readonly Error Invalid = new("invalid-tracking-token", "The tracking link is invalid or has expired.");

    private static readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(30);

    // A token signed with a key we do not know could be a rotated key or an attack. We re-fetch at most this often, so anonymous callers cannot make us hammer Orders.
    private static readonly TimeSpan _minRefetch = TimeSpan.FromSeconds(30);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private JsonWebKeySet _keys = new();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>The grant, or <see cref="Invalid"/>. Orders being unreachable throws <see cref="HttpRequestException"/>: that is an outage, not a bad token.</summary>
    public async Task<Result> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || !_handler.CanReadToken(token))
        {
            return Invalid;
        }

        // Three dot-separated segments pass CanReadToken, but their contents may still not decode ("a.b.c"): anonymous input must end as "invalid", never as a crash.
        string? kid;
        try
        {
            kid = _handler.ReadJsonWebToken(token).Kid;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return Invalid;
        }

        await EnsureKeyAsync(kid, cancellationToken);

        var now = time.GetUtcNow();
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            IssuerSigningKeys = _keys.GetSigningKeys(),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = _clockSkew,
            // The business clock (TimeProvider), so a test can move time without waiting.
            LifetimeValidator = (notBefore, expires, _, _) =>
                expires is { } exp && now <= new DateTimeOffset(exp, TimeSpan.Zero) + _clockSkew
                && (notBefore is not { } nb || now >= new DateTimeOffset(nb, TimeSpan.Zero) - _clockSkew),
        });

        if (!result.IsValid
            || !result.Claims.TryGetValue(OrderIdClaim, out var raw)
            || !Guid.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var orderId)
            || result.SecurityToken is not JsonWebToken jwt)
        {
            return Invalid;
        }

        return new TrackingGrant(orderId, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
    }

    public void Dispose() => _refresh.Dispose();

    private async Task EnsureKeyAsync(string? kid, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var known = kid is not null && _keys.Keys.Any(k => k.Kid == kid);
        var fresh = now - _fetchedAt < options.Value.JwksCacheLifetime;
        if (known && fresh)
        {
            return;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            now = time.GetUtcNow();
            known = kid is not null && _keys.Keys.Any(k => k.Kid == kid);
            fresh = now - _fetchedAt < options.Value.JwksCacheLifetime;

            // Fetch when we hold no keys, when the cache is old, or when the token names a key we lack (rotation); but not more often than _minRefetch.
            var mayRefetch = now - _fetchedAt >= _minRefetch;
            if ((_keys.Keys.Count == 0 || !fresh || !known) && mayRefetch)
            {
                using var http = httpFactory.CreateClient(HttpClientName);
                var json = await http.GetStringAsync(new Uri(options.Value.OrdersAddress, JwksPath), cancellationToken);
                _keys = new JsonWebKeySet(json);
                _fetchedAt = now;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }
}
