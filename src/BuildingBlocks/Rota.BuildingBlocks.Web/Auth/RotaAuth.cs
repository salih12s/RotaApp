using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Rota.BuildingBlocks.Web.Auth;

/// <summary>Role names as they appear in tokens (docs/06-security-and-errors.md §1).</summary>
public static class RotaRoles
{
    public const string Merchant = "merchant";
    public const string Courier = "courier";
    public const string Dispatcher = "dispatcher";

    /// <summary>Another Rota service calling over the internal network (Dispatch -> Tracking); a client-credentials token in F08.</summary>
    public const string Service = "service";
}

/// <summary>Claim types; short JWT names, the same ones Keycloak issues (F08).</summary>
public static class RotaClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string MerchantId = "merchant_id";

    public static string? GetSubject(this ClaimsPrincipal user) => user?.FindFirst(Subject)?.Value;

    public static string? GetMerchantId(this ClaimsPrincipal user) => user?.FindFirst(MerchantId)?.Value;

    public static Guid? GetCourierId(this ClaimsPrincipal user) =>
        user is not null && user.IsInRole(RotaRoles.Courier) && Guid.TryParse(user.GetSubject(), out var id) ? id : null;
}

/// <summary>Policies from docs/06-security-and-errors.md §2. A missing role is 403; ownership is checked in handlers (404).</summary>
public static class RotaPolicies
{
    public const string MerchantOnly = nameof(MerchantOnly);
    public const string CourierOnly = nameof(CourierOnly);
    public const string DispatcherOnly = nameof(DispatcherOnly);
    public const string ServiceOnly = nameof(ServiceOnly);
    public const string MerchantOrDispatcher = nameof(MerchantOrDispatcher);
    public const string CourierOrDispatcher = nameof(CourierOrDispatcher);
    public const string AnyActor = nameof(AnyActor);
}

/// <summary>Settings of the <c>Auth</c> configuration section: where Keycloak is and who a token must be meant for.</summary>
public sealed class RotaAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC issuer, e.g. <c>http://localhost:8180/realms/rota</c>. Signing keys are fetched from its discovery document, so key rotation needs no deploy.</summary>
    public string? Authority { get; set; }

    /// <summary>The <c>aud</c> a token must carry. Keycloak adds it with an audience mapper; without the check a token issued for another API would be accepted.</summary>
    public string Audience { get; set; } = "rota-api";

    /// <summary>Metadata and keys must come over https. Default true; switch off only for a Keycloak on a closed network (local compose, tests).</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class RotaAuthExtensions
{
    private const string BearerOrDevScheme = "BearerOrDev";

    /// <summary>
    /// Authentication for a service (docs/06 §1). With <c>Auth:Authority</c> set, Keycloak JWTs are validated here as well as at the
    /// gateway (defense in depth). In Development <c>X-Dev-User</c> (ADR-006) is accepted alongside; anywhere else the header is
    /// ignored, and a missing <c>Auth:Authority</c> stops the start-up instead of silently leaving the service open.
    /// </summary>
    public static IHostApplicationBuilder AddRotaAuth(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(RotaAuthOptions.SectionName).Get<RotaAuthOptions>() ?? new RotaAuthOptions();
        var development = builder.Environment.IsDevelopment();
        var hasAuthority = !string.IsNullOrWhiteSpace(options.Authority);

        if (!hasAuthority)
        {
            if (!development)
            {
                throw new InvalidOperationException(
                    $"'{RotaAuthOptions.SectionName}:Authority' is required outside Development; the environment is '{builder.Environment.EnvironmentName}'.");
            }

            // Development without Keycloak: the header scheme alone, exactly as in F01-F07 (and in the integration tests).
            return builder.AddRotaDevAuth();
        }

        var auth = builder.Services.AddAuthentication(development ? BearerOrDevScheme : JwtBearerDefaults.AuthenticationScheme);
        auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            jwt.Authority = options.Authority;
            jwt.Audience = options.Audience;
            jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
            jwt.MapInboundClaims = false; // keep "sub" / "role" / "merchant_id" as Keycloak writes them
            jwt.TokenValidationParameters.NameClaimType = RotaClaims.Subject;
            jwt.TokenValidationParameters.RoleClaimType = RotaClaims.Role;
            jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
        });

        if (development)
        {
            auth.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
            auth.AddPolicyScheme(BearerOrDevScheme, BearerOrDevScheme, policy =>
                policy.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(DevAuthHandler.HeaderName)
                        ? DevAuthHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme);
        }

        AddRotaPolicies(builder.Services);
        return builder;
    }

    /// <summary>
    /// Registers <see cref="DevAuthHandler"/> and the Rota policies. Refuses to start outside
    /// Development (ADR-006): a header-trusting scheme in production would let anyone be anyone.
    /// </summary>
    public static IHostApplicationBuilder AddRotaDevAuth(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{DevAuthHandler.SchemeName} is only allowed in Development (ADR-006); the environment is '{builder.Environment.EnvironmentName}'.");
        }

        builder.Services
            .AddAuthentication(DevAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);

        AddRotaPolicies(builder.Services);
        return builder;
    }

    private static void AddRotaPolicies(IServiceCollection services) =>
        services.AddAuthorizationBuilder()
            .AddPolicy(RotaPolicies.MerchantOnly, p => p.RequireRole(RotaRoles.Merchant))
            .AddPolicy(RotaPolicies.CourierOnly, p => p.RequireRole(RotaRoles.Courier))
            .AddPolicy(RotaPolicies.DispatcherOnly, p => p.RequireRole(RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.ServiceOnly, p => p.RequireRole(RotaRoles.Service))
            .AddPolicy(RotaPolicies.MerchantOrDispatcher, p => p.RequireRole(RotaRoles.Merchant, RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.CourierOrDispatcher, p => p.RequireRole(RotaRoles.Courier, RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.AnyActor, p => p.RequireRole(RotaRoles.Merchant, RotaRoles.Courier, RotaRoles.Dispatcher));
}
