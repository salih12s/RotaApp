using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.Dispatch.Api.Infrastructure;
using Rota.Dispatch.IntegrationTests;
using Rota.TestKit.Postgres;
using Testcontainers.PostgreSql;
using StackExchange.Redis;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(DispatchFixture))]

namespace Rota.Dispatch.IntegrationTests;

/// <summary>
/// One PostgreSQL and one Redis for the whole test assembly (started once, slow); every test gets its <b>own database</b>
/// inside that PostgreSQL. The timeout job looks at <i>all</i> due offers in a database, so tests that share one would
/// expire each other's offers. Redis is shared safely: channels are named after the courier, and ids are unique per test.
/// </summary>
public sealed class DispatchFixture : IAsyncLifetime
{
    /// <summary>Same image as deploy/docker-compose.yml.</summary>
    public const string RedisImage = "redis:8.10.3-alpine";

    // The race tests run 100 concurrent requests, each with its own connection; the default limit of 100 would be tight.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresTestContainer.Image)
        .WithCommand("-c", "max_connections=500")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder(RedisImage).Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    /// <summary>The test's own look into Redis (the service under test has its own connection).</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
    }

    /// <summary>A new, migrated, empty Dispatch database. The pool is capped per database so 100 parallel requests queue instead of exhausting the server.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await _postgres.CreateDatabaseAsync($"dispatch_{Guid.NewGuid():N}", cancellationToken);
        connectionString += ";Maximum Pool Size=60";

        var options = new DbContextOptionsBuilder<DispatchDbContext>();
        options.UseRotaNpgsql(connectionString);
        await using var db = new DispatchDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken);
        return connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        await Redis.DisposeAsync();
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
