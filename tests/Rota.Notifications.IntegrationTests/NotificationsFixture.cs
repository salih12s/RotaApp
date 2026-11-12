using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.Notifications.Api.Infrastructure;
using Rota.Notifications.IntegrationTests;
using Rota.TestKit.Postgres;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(NotificationsFixture))]

namespace Rota.Notifications.IntegrationTests;

/// <summary>
/// One PostgreSQL for the whole test assembly (slow to start); every test gets its <b>own database</b> in it. The delivery worker looks
/// at all due rows of a database, so tests that shared one would deliver (and fail) each other's webhooks.
/// </summary>
public sealed class NotificationsFixture : IAsyncLifetime
{
    // The bulkhead test runs many parallel connections.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresTestContainer.Image)
        .WithCommand("-c", "max_connections=300")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await _postgres.CreateDatabaseAsync($"notifications_{Guid.NewGuid():N}", cancellationToken);
        var options = new DbContextOptionsBuilder<NotificationsDbContext>();
        options.UseRotaNpgsql(connectionString);
        await using var db = new NotificationsDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken);
        return connectionString;
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}
