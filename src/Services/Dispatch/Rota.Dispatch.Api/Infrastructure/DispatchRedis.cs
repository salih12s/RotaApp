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

            // Connect() waits this long for a Redis that is not there (the default is 5 s) before it gives up and retries in the background.
            options.ConnectTimeout = Math.Min(options.ConnectTimeout, 1_000);
            return ConnectionMultiplexer.Connect(options);
        });

        // Whoever asks for the connection first pays for the wait, and that is often a message handler or a commit. Paying at startup,
        // in the background, keeps it off those paths: the first message after a start with Redis down is no longer held up.
        builder.Services.AddHostedService<RedisWarmup>();

        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis");
        return builder;
    }
}

/// <summary>Creates the Redis connection in the background when the service starts; never delays startup and never fails it.</summary>
public sealed class RedisWarmup(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => services.GetRequiredService<IConnectionMultiplexer>(), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
