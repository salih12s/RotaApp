using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Rota.BuildingBlocks.Persistence;

public static class PersistenceExtensions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Registers a service's own DbContext on PostgreSQL with Rota's conventions:
    /// snake_case names (tables match docs/04-data-model.md) and one database per service.
    /// </summary>
    public static IHostApplicationBuilder AddRotaDbContext<TContext>(this IHostApplicationBuilder builder, string connectionName)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        builder.Services.AddDbContext<TContext>((services, options) =>
        {
            // Read when the context is first created, so tests can override the connection string.
            var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName)
                ?? throw new InvalidOperationException($"Connection string '{connectionName}' is not configured.");

            // Interceptors registered in DI join the context; tests use this to inject failures.
            options.UseRotaNpgsql(connectionString).AddInterceptors(services.GetServices<IInterceptor>());
        });

        return builder;
    }

    /// <summary>Shared by the runtime registration and design-time factories, so migrations see the same model.</summary>
    public static DbContextOptionsBuilder UseRotaNpgsql(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention();
    }
}
