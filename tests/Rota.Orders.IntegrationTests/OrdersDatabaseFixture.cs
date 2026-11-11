using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.Orders.Api.Infrastructure;
using Rota.Orders.IntegrationTests;
using Rota.TestKit.Postgres;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(OrdersDatabaseFixture))]

namespace Rota.Orders.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the whole test assembly, migrated once. Test classes still run
/// in parallel: isolation comes from unique merchant ids per test, not from cleaning tables.
/// </summary>
public sealed class OrdersDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = PostgresTestContainer.Create();

    public string ConnectionString => _container.GetConnectionString();

    public PostgreSqlContainer Container => _container;

    /// <summary>The API shared by tests that need no special wiring (started once, reused).</summary>
    public OrdersApiFactory Api { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        Api = new OrdersApiFactory(ConnectionString);
    }

    public OrdersDbContext CreateDbContext() => CreateDbContext(ConnectionString);

    public static OrdersDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>();
        options.UseRotaNpgsql(connectionString);
        return new OrdersDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await _container.DisposeAsync();
    }
}
