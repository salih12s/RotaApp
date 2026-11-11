using Microsoft.Extensions.Options;
using Npgsql;
using Rota.Tracking.Api.Domain;

namespace Rota.Tracking.Api.Infrastructure;

public sealed record PartitionReport(IReadOnlyList<string> Created, IReadOnlyList<string> Dropped);

/// <summary>
/// Keeps the daily partitions of <c>location_points</c>: creates the coming days, drops those past the
/// retention. Idempotent, so it can run as often as wanted and by several instances at once.
/// </summary>
public sealed class PartitionMaintainer(NpgsqlDataSource database, IOptions<TrackingOptions> options)
{
    /// <param name="now">Passed in (from the TimeProvider), so a test moves the "calendar" without waiting.</param>
    /// <param name="cancellationToken">Cancels the database calls.</param>
    public async Task<PartitionReport> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // One instance at a time: two creating the same partition would collide. The lock is released at commit.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('tracking-partition-maintenance'))", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = await ListPartitionsAsync(connection, transaction, cancellationToken);
        var created = new List<string>();
        var dropped = new List<string>();

        foreach (var day in PartitionPlan.RequiredDays(now, settings.PartitionDaysAhead))
        {
            var name = PartitionPlan.NameFor(day);
            if (existing.Contains(name))
            {
                continue;
            }

            var (from, to) = PartitionPlan.Bounds(day);

            // Identifiers cannot be SQL parameters. Name and bounds are built from a DateOnly (digits and
            // dashes only), never from input.
#pragma warning disable CA2100
            await using var create = new NpgsqlCommand(
                $"CREATE TABLE {name} PARTITION OF {PartitionPlan.TableName} FOR VALUES FROM ('{from}') TO ('{to}')", connection, transaction);
#pragma warning restore CA2100
            await create.ExecuteNonQueryAsync(cancellationToken);
            created.Add(name);
        }

        foreach (var name in existing.Order(StringComparer.Ordinal))
        {
            if (PartitionPlan.TryParseDay(name, out var day) && PartitionPlan.IsExpired(day, now, settings.HistoryRetention))
            {
#pragma warning disable CA2100
                await using var drop = new NpgsqlCommand($"DROP TABLE {name}", connection, transaction);
#pragma warning restore CA2100
                await drop.ExecuteNonQueryAsync(cancellationToken);
                dropped.Add(name);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PartitionReport(created, dropped);
    }

    private static async Task<HashSet<string>> ListPartitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT child.relname
            FROM pg_inherits link
            JOIN pg_class child ON child.oid = link.inhrelid
            JOIN pg_class parent ON parent.oid = link.inhparent
            WHERE parent.relname = @parent
            """, connection, transaction);
        command.Parameters.AddWithValue("parent", PartitionPlan.TableName);

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

/// <summary>
/// Runs the maintenance once before the service takes traffic (so the first points find their partition),
/// then on an interval. A failure is logged and retried; it must not stop the service from starting.
/// </summary>
public sealed partial class PartitionMaintenanceService(
    PartitionMaintainer maintainer, TimeProvider time, IOptions<TrackingOptions> options, ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    private static readonly TimeSpan _retryAfterFailure = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await TryRunAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = await TryRunAsync(stoppingToken);
            try
            {
                await Task.Delay(succeeded ? options.Value.MaintenanceInterval : _retryAfterFailure, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> TryRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await maintainer.RunAsync(time.GetUtcNow(), cancellationToken);
            if (report.Created.Count > 0 || report.Dropped.Count > 0)
            {
                LogRan(logger, report.Created.Count, report.Dropped.Count);
            }

            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            LogFailed(logger, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Partition maintenance: created {Created}, dropped {Dropped}")]
    private static partial void LogRan(ILogger logger, int created, int dropped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Partition maintenance failed; will retry")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
