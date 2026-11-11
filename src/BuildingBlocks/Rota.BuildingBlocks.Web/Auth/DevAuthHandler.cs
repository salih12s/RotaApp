using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Rota.BuildingBlocks.Web.Auth;

/// <summary>
/// Development/test-only authentication (ADR-006): the caller states who they are in a header,
/// <c>X-Dev-User: merchant:&lt;merchantId&gt;</c>, <c>courier:&lt;guid&gt;</c> or <c>dispatcher:&lt;id&gt;</c>.
/// It produces the same claims Keycloak will issue in F08, so authorization code does not change.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAuth";
    public const string HeaderName = "X-Dev-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            // No header = anonymous; endpoints that require a user answer 401.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = ParseClaims(values.ToString());
        if (claims is null)
        {
            return Task.FromResult(AuthenticateResult.Fail($"{HeaderName} must be '<role>:<id>'."));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, RotaClaims.Subject, RotaClaims.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    internal static List<Claim>? ParseClaims(string header)
    {
        var separator = header.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == header.Length - 1)
        {
            return null;
        }

        var role = header[..separator];
        var id = header[(separator + 1)..];

        return role switch
        {
            RotaRoles.Merchant =>
            [
                new(RotaClaims.Subject, id),
                new(RotaClaims.Role, RotaRoles.Merchant),
                new(RotaClaims.MerchantId, id),
            ],
            RotaRoles.Courier when Guid.TryParse(id, out _) =>
            [
                new(RotaClaims.Subject, id),
                new(RotaClaims.Role, RotaRoles.Courier),
            ],
            RotaRoles.Dispatcher =>
            [
                new(RotaClaims.Subject, id),
                new(RotaClaims.Role, RotaRoles.Dispatcher),
            ],
            _ => null,
        };
    }
}
