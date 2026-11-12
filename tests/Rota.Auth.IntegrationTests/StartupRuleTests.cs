extern alias OrdersApi;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Rota.BuildingBlocks.Web.Auth;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// PASS 4: a header-trusting scheme must never be live in production (ADR-006). The start-up rules are plain configuration logic,
/// so they are tested without containers, and once through the real Orders Program.
/// </summary>
[Trait("Category", "Auth")]
public sealed class StartupRuleTests
{
    private static WebApplicationBuilder BuilderFor(string environment, string? authority = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        if (authority is not null)
        {
            builder.Configuration["Auth:Authority"] = authority;
        }

        return builder;
    }

    private static async Task<string[]> SchemesAsync(WebApplicationBuilder builder)
    {
        await using var app = builder.Build();
        var schemes = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        return [.. schemes.Select(s => s.Name)];
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevAuth_refuses_to_register_outside_Development(string environment) =>
        Should.Throw<InvalidOperationException>(() => BuilderFor(environment).AddRotaDevAuth())
            .Message.ShouldContain("only allowed in Development");

    [Fact]
    public void Production_without_an_authority_refuses_to_start() =>
        Should.Throw<InvalidOperationException>(() => BuilderFor("Production").AddRotaAuth())
            .Message.ShouldContain("Auth:Authority");

    [Fact]
    public async Task Production_with_an_authority_has_the_bearer_scheme_and_no_DevAuth()
    {
        var builder = BuilderFor("Production", "https://id.example.test/realms/rota");
        builder.AddRotaAuth();

        var schemes = await SchemesAsync(builder);

        schemes.ShouldContain("Bearer");
        schemes.ShouldNotContain(DevAuthHandler.SchemeName);
    }

    [Fact]
    public async Task Development_with_an_authority_accepts_both_a_token_and_the_dev_header()
    {
        var builder = BuilderFor("Development", "http://localhost:8180/realms/rota");
        builder.AddRotaAuth();

        var schemes = await SchemesAsync(builder);

        schemes.ShouldContain("Bearer");
        schemes.ShouldContain(DevAuthHandler.SchemeName);
    }

    [Fact]
    public void The_real_Orders_service_does_not_start_in_Production_without_an_authority()
    {
        using var host = new ServiceHost<OrdersApi::Program>(new Dictionary<string, string> { ["environment"] = "Production" });

        Should.Throw<InvalidOperationException>(() => host.CreateClient()).Message.ShouldContain("Auth:Authority");
    }
}
