using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.OpsApi.Infrastructure;
using Rota.OpsApi.IntegrationTests;
using Rota.TestKit.Postgres;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(OpsFixture))]

namespace Rota.OpsApi.IntegrationTests;

/// <summary>One PostgreSQL and one Redis for the whole assembly, the database migrated once. Isolation comes from unique order and courier ids, not from cleaning tables.</summary>
public sealed class OpsFixture : IAsyncLifetime
{
    /// <summary>Same image as deploy/docker-compose.yml.</summary>
    public const string RedisImage = "redis:8.10.3-alpine";

    private readonly PostgreSqlContainer _postgres = PostgresTestContainer.Create();
    private readonly RedisContainer _redis = new RedisBuilder(RedisImage).Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_redis.StartAsync(), _postgres.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>A new, migrated, empty database next to the shared one: for a test that counts everything (the overview).</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = await _postgres.CreateDatabaseAsync($"ops_{Guid.NewGuid():N}", cancellationToken);
        var options = new DbContextOptionsBuilder<OpsDbContext>();
        options.UseRotaNpgsql(connection);
        await using var db = new OpsDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken);
        return connection;
    }

    public OpsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OpsDbContext>();
        options.UseRotaNpgsql(ConnectionString);
        return new OpsDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
