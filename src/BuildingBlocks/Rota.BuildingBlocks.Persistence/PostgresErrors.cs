using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Rota.BuildingBlocks.Persistence;

public static class PostgresErrors
{
    /// <summary>
    /// True when the save failed on a unique constraint (SQLSTATE 23505), optionally a specific one.
    /// This is how a concurrent duplicate insert surfaces after PostgreSQL made it wait for the first.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException exception, string? constraintName = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && (constraintName is null || string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal));
    }
}
