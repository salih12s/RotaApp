using Microsoft.AspNetCore.Authorization;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Gateway.RateLimiting;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaAuth();

// Every proxied route needs a signed-in caller unless the route says "AuthorizationPolicy": "anonymous".
// Roles are NOT decided here: each service checks its own policies, so the gateway can be bypassed without opening anything (docs/06 §1).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName);
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
    var options = ConfigurationOptions.Parse(connection);
    options.AbortOnConnectFail = false; // the gateway starts even if Redis is late; the limiter fails open meanwhile
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<RedisTokenBucket>();

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RateLimitMiddleware>();
app.MapReverseProxy();

app.Run();

/// <summary>Visible to the test project (WebApplicationFactory).</summary>
public partial class Program;
