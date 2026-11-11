using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Rota.Orders.IntegrationTests.Support;

/// <summary>
/// Fault injection: lets the INSERT into <c>orders</c> run inside the transaction, then throws once,
/// as if the process failed between "order written" and "commit".
/// </summary>
public sealed class FailOnceAfterOrderInsert : DbCommandInterceptor
{
    private int _armed = 1;

    public bool Fired => _armed == 0;

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandText.Contains("INSERT INTO orders", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new InvalidOperationException("Injected failure after the order insert.");
        }

        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// Counts commands that failed on a unique constraint. Lets a race test prove that the race
/// actually happened, instead of passing because every request took the sequential fast path.
/// </summary>
public sealed class UniqueViolationCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            Interlocked.Increment(ref _count);
        }

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }
}
