using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Rota.Orders.Api.Infrastructure;

/// <summary>
/// Signs tracking tokens with ES256 (ADR-007). Only Orders holds the private key; Ops API gets
/// the public key from <c>/.well-known/tracking-jwks</c> and can verify tokens but never mint them.
/// </summary>
public sealed class TrackingTokenIssuer : IDisposable
{
    public const string Issuer = "rota-orders";
    public const string Audience = "rota-tracking";
    public const string OrderIdClaim = "order_id";

    private readonly ECDsa _privateKey;
    private readonly SigningCredentials _credentials;
    private readonly OrdersOptions _options;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public TrackingTokenIssuer(IOptions<OrdersOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        _privateKey = ECDsa.Create();
        _privateKey.ImportFromPem(_options.TrackingSigningKey);
        if (_privateKey.KeySize != 256)
        {
            throw new InvalidOperationException("The tracking signing key must be an EC P-256 key (ES256).");
        }

        PublicJwk = CreatePublicJwk(_privateKey);
        var securityKey = new ECDsaSecurityKey(_privateKey) { KeyId = PublicJwk.Kid };
        _credentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256)
        {
            // Own provider cache: the shared default caches by key thumbprint and would hand a later
            // issuer with the same key a signer bound to this instance's (by then disposed) ECDsa.
            CryptoProviderFactory = new CryptoProviderFactory(),
        };
    }

    /// <summary>The public half only (no <c>d</c>), as served in the JWKS.</summary>
    public PublicEcJwk PublicJwk { get; }

    /// <summary>Times come from the caller (TimeProvider); the handler never reads the clock itself.</summary>
    public string CreateToken(Guid orderId, DateTimeOffset now) =>
        _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + _options.TrackingTokenLifetime).UtcDateTime,
            Claims = new Dictionary<string, object> { [OrderIdClaim] = orderId.ToString("D", CultureInfo.InvariantCulture) },
            SigningCredentials = _credentials,
        });

    public string CreateTrackingUrl(Guid orderId, DateTimeOffset now) =>
        new Uri(_options.TrackingBaseUrl, CreateToken(orderId, now)).ToString();

    public void Dispose() => _privateKey.Dispose();

    private static PublicEcJwk CreatePublicJwk(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        using var publicKey = ECDsa.Create(parameters);
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicKey));

        // kid = RFC 7638 thumbprint: stable for the same key, changes when the key is rotated.
        var kid = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
        return new PublicEcJwk("EC", "P-256", jwk.X, jwk.Y, kid, "sig", "ES256");
    }
}

/// <summary>An EC public key in JWK form (RFC 7517). Has no private member by construction.</summary>
public sealed record PublicEcJwk(string Kty, string Crv, string X, string Y, string Kid, string Use, string Alg);
