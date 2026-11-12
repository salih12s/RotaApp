using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
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

public static class RotaAuthExtensions
{
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

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(RotaPolicies.MerchantOnly, p => p.RequireRole(RotaRoles.Merchant))
            .AddPolicy(RotaPolicies.CourierOnly, p => p.RequireRole(RotaRoles.Courier))
            .AddPolicy(RotaPolicies.DispatcherOnly, p => p.RequireRole(RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.ServiceOnly, p => p.RequireRole(RotaRoles.Service))
            .AddPolicy(RotaPolicies.MerchantOrDispatcher, p => p.RequireRole(RotaRoles.Merchant, RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.CourierOrDispatcher, p => p.RequireRole(RotaRoles.Courier, RotaRoles.Dispatcher))
            .AddPolicy(RotaPolicies.AnyActor, p => p.RequireRole(RotaRoles.Merchant, RotaRoles.Courier, RotaRoles.Dispatcher));

        return builder;
    }
}
