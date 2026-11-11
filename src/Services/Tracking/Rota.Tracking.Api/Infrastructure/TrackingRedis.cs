using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Rota.Tracking.Api.Infrastructure;

public static class TrackingRedis
{
    public const string ConnectionName = "Redis";

    /// <summary>
    /// One shared Redis connection (the client multiplexes all calls over it). <c>AbortOnConnectFail=false</c>:
    /// the service starts even if Redis is down and keeps retrying in the background; calls fail until it is
    /// back, and <c>/health/ready</c> says so. Redis holds only derived data (ADR-001), so this is safe.
    /// </summary>
    public static IHostApplicationBuilder AddTrackingRedis(this IHostApplicationBuilder builder)
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
