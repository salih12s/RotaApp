using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rota.Orders.Api.Infrastructure;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.UnitTests.Infrastructure;

public sealed class TrackingTokenIssuerTests : IDisposable
{
    private readonly TrackingTokenIssuer _issuer = new(Options.Create(new OrdersOptions
    {
        TrackingSigningKey = OrdersTestSettings.TrackingSigningKeyPem,
        TrackingTokenLifetime = TimeSpan.FromHours(24),
    }));

    [Fact]
    public async Task CreateToken_VerifiesWithPublicJwkOnly_AndCarriesOrderAndExpiry()
    {
        var orderId = Guid.CreateVersion7();
        var now = TestClock.Start;

        var token = _issuer.CreateToken(orderId, now);

        // Verify the way Ops API will: only the public JWK, never the private key.
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, ValidationFor(now.AddHours(1)));
        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        result.Claims[TrackingTokenIssuer.OrderIdClaim].ShouldBe(orderId.ToString());
        var jwt = (JsonWebToken)result.SecurityToken;
        jwt.Alg.ShouldBe("ES256");
        jwt.Kid.ShouldBe(_issuer.PublicJwk.Kid);
        jwt.ValidTo.ShouldBe(now.AddHours(24).UtcDateTime);
    }

    [Fact]
    public async Task CreateToken_AfterLifetime_IsRejected()
    {
        var token = _issuer.CreateToken(Guid.CreateVersion7(), TestClock.Start);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, ValidationFor(TestClock.Start.AddHours(25)));

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidLifetimeException>();
    }

    [Fact]
    public async Task CreateToken_SignedByAnotherKey_IsRejected()
    {
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = new TrackingTokenIssuer(Options.Create(new OrdersOptions { TrackingSigningKey = otherKey.ExportPkcs8PrivateKeyPem() }));
        var forged = other.CreateToken(Guid.CreateVersion7(), TestClock.Start);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(forged, ValidationFor(TestClock.Start));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void PublicJwk_IsAnEcP256KeyWithStableThumbprintKid()
    {
        _issuer.PublicJwk.ShouldSatisfyAllConditions(
            k => k.Kty.ShouldBe("EC"),
            k => k.Crv.ShouldBe("P-256"),
            k => k.Alg.ShouldBe("ES256"),
            k => k.Kid.ShouldNotBeNullOrWhiteSpace());
        using var again = new TrackingTokenIssuer(Options.Create(new OrdersOptions { TrackingSigningKey = OrdersTestSettings.TrackingSigningKeyPem }));
        again.PublicJwk.Kid.ShouldBe(_issuer.PublicJwk.Kid);
    }

    public void Dispose() => _issuer.Dispose();

    private TokenValidationParameters ValidationFor(DateTimeOffset now)
    {
        var jwk = new JsonWebKey
        {
            Kty = _issuer.PublicJwk.Kty,
            Crv = _issuer.PublicJwk.Crv,
            X = _issuer.PublicJwk.X,
            Y = _issuer.PublicJwk.Y,
            Kid = _issuer.PublicJwk.Kid,
        };

        return new TokenValidationParameters
        {
            ValidIssuer = TrackingTokenIssuer.Issuer,
            ValidAudience = TrackingTokenIssuer.Audience,
            IssuerSigningKey = jwk,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.Zero,
            // The test clock, not the machine clock: the expiry check stays deterministic.
            LifetimeValidator = (notBefore, expires, _, _) => notBefore <= now.UtcDateTime && now.UtcDateTime < expires,
        };
    }
}
