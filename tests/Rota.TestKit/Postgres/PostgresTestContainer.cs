using Npgsql;
using Testcontainers.PostgreSql;

namespace Rota.TestKit.Postgres;

/// <summary>
/// A real PostgreSQL in Docker for integration tests. Race, transaction and lock behaviour
/// cannot be tested with mocks or an in-memory provider, so tests talk to the real engine.
/// </summary>
public static class PostgresTestContainer
{
    /// <summary>Same image as deploy/docker-compose.yml, so tests and local dev run one engine version.</summary>
    public const string Image = "postgres:18.6-alpine";

    public static PostgreSqlContainer Create() => new PostgreSqlBuilder(Image).Build();

    /// <summary>Creates an empty database next to the default one, e.g. to apply migrations from zero.</summary>
    public static async Task<string> CreateDatabaseAsync(
        this PostgreSqlContainer container, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (!name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException("Use lower-case letters, digits and '_' only.", nameof(name));
        }

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        // Identifiers cannot be SQL parameters; the name is validated above.
#pragma warning disable CA2100
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
