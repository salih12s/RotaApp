using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rota.BuildingBlocks.Persistence;
using Rota.Tracking.Api.Infrastructure;
using Rota.Tracking.IntegrationTests;
using Rota.TestKit.Postgres;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(TrackingFixture))]

namespace Rota.Tracking.IntegrationTests;

/// <summary>
/// One Redis and one PostgreSQL for the whole test assembly. Isolation between tests comes from unique
/// courier ids, not from cleaning data (tests run in parallel). A test that must own the whole database
/// (partition maintenance) creates its own with <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class TrackingFixture : IAsyncLifetime
{
    /// <summary>Same image as deploy/docker-compose.yml.</summary>
    public const string RedisImage = "redis:8.10.3-alpine";

    private readonly RedisContainer _redis = new RedisBuilder(RedisImage).Build();
    private readonly PostgreSqlContainer _postgres = PostgresTestContainer.Create();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    /// <summary>The test's own look into Redis (the service under test has its own connection).</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    /// <summary>The test's own look into the shared database.</summary>
    public NpgsqlDataSource Database { get; private set; } = null!;

    /// <summary>The API shared by tests that need no special wiring.</summary>
    public TrackingApiFactory Api { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_redis.StartAsync(), _postgres.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);

        await MigrateAsync(PostgresConnectionString);
        Database = NpgsqlDataSource.Create(PostgresConnectionString);
        Api = new TrackingApiFactory(RedisConnectionString, PostgresConnectionString);
    }

    /// <summary>A new, migrated, empty database next to the shared one.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await _postgres.CreateDatabaseAsync($"tracking_{Guid.NewGuid():N}", cancellationToken);
        await MigrateAsync(connectionString);
        return connectionString;
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TrackingDbContext>();
        options.UseRotaNpgsql(connectionString);
        await using var db = new TrackingDbContext(options.Options);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Redis.DisposeAsync();
        await Database.DisposeAsync();
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
