using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Rota.Dispatch.Api.Infrastructure;

public static class DispatchRedis
{
    public const string ConnectionName = "Redis";

    /// <summary>
    /// One shared Redis connection, used only for Pub/Sub (<c>dsp:courier:{id}:offers</c>). Offers themselves
    /// live in PostgreSQL: Redis down means pushes are late, never that an offer is lost (docs/04 "Dispatch Redis").
    /// <c>AbortOnConnectFail=false</c>: the service starts without Redis and reconnects in the background.
    /// </summary>
    public static IHostApplicationBuilder AddDispatchRedis(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
                ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is not configured.");

            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis");
        return builder;
    }
}

/// <summary>Readiness: can we reach Redis right now? (Liveness stays "the process is up".)</summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis answered in {latency.TotalMilliseconds:0.#} ms");
        }
        catch (RedisException ex)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable", ex);
        }
    }
}
