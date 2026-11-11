using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Rota.Tracking.Api.Infrastructure;

/// <summary>One position as stored in <c>location_points</c>.</summary>
public sealed record LocationRecord(
    Guid CourierId, long Seq, double Lat, double Lon, float? AccuracyMeters, DateTimeOffset RecordedAt, DateTimeOffset ReceivedAt);

public static class TrackingTelemetry
{
    public const string MeterName = "Rota.Tracking";

    private static readonly Meter _meter = new(MeterName);

    public static readonly Counter<long> HistoryWritten = _meter.CreateCounter<long>("rota.tracking.history_written", description: "Points inserted into location_points");
    public static readonly Counter<long> HistoryDuplicates = _meter.CreateCounter<long>("rota.tracking.history_duplicates", description: "Points that were already stored (same courier, seq, time)");
    public static readonly Counter<long> HistoryDropped = _meter.CreateCounter<long>("rota.tracking.history_dropped", description: "Points given up on after the database kept failing");
}

/// <summary>
/// Writes the position history: queue → batch → <c>COPY</c> → staging → <c>INSERT ... ON CONFLICT DO NOTHING</c>.
/// </summary>
/// <remarks>
/// Why not EF <c>SaveChanges</c> per point: with hundreds of couriers sending every 3 s, that is hundreds of
/// round trips and transactions per second for a table nobody reads on the hot path. One batch is one
/// round trip. Why a staging table: <c>COPY</c> is the fastest way in but cannot skip duplicates, so it
/// lands in an unconstrained table first and the insert into the real table drops the repeats.
/// History is best effort: the current position (Redis) is what dispatching needs. A batch that still
/// fails after the retries is dropped and counted, rather than stalling every courier's stream.
/// </remarks>
public sealed partial class LocationHistoryWriter : BackgroundService
{
    private static readonly TimeSpan[] _retryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly LocationBatcher<LocationRecord> _batcher;
    private readonly NpgsqlDataSource _database;
    private readonly ILogger<LocationHistoryWriter> _logger;

    public LocationHistoryWriter(NpgsqlDataSource database, IOptions<TrackingOptions> options, ILogger<LocationHistoryWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _database = database;
        _logger = logger;

        // The batch window is an infrastructure timer, so it runs on the real clock. The injected TimeProvider
        // is for business time (received_at, the accepted window): tests freeze it, and a frozen batch
        // timer would never fire and hold points back forever.
        _batcher = new LocationBatcher<LocationRecord>(
            settings.HistoryQueueCapacity, settings.HistoryBatchSize, settings.HistoryMaxWait, TimeProvider.System);
    }

    /// <summary>Hands a point over for history. Waits if the queue is full (back-pressure on the stream).</summary>
    public ValueTask EnqueueAsync(LocationRecord record, CancellationToken cancellationToken) => _batcher.EnqueueAsync(record, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The reader is not tied to the stopping token: on shutdown the queue is completed and drained,
        // so points already accepted are not thrown away.
        await foreach (var batch in _batcher.ReadBatchesAsync(CancellationToken.None))
        {
            await WriteWithRetriesAsync(batch, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _batcher.Complete();
        await base.StopAsync(cancellationToken);
    }

    private async Task WriteWithRetriesAsync(IReadOnlyList<LocationRecord> batch, CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var inserted = await WriteBatchAsync(batch, CancellationToken.None);
                TrackingTelemetry.HistoryWritten.Add(inserted);
                TrackingTelemetry.HistoryDuplicates.Add(batch.Count - inserted);
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                if (attempt >= _retryDelays.Length || stoppingToken.IsCancellationRequested)
                {
                    TrackingTelemetry.HistoryDropped.Add(batch.Count);
                    LogDropped(_logger, ex, batch.Count);
                    return;
                }

                await Task.Delay(_retryDelays[attempt], CancellationToken.None);
            }
        }
    }

    /// <returns>How many rows were really inserted (the rest were repeats).</returns>
    internal async Task<int> WriteBatchAsync(IReadOnlyList<LocationRecord> batch, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);

        // Everything in one transaction. Concurrent Tracking instances share the staging table and stay out
        // of each other's way: a transaction sees only its own uncommitted staging rows, and each one ends
        // with the table emptied again.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var import = await connection.BeginBinaryImportAsync(
            "COPY location_points_staging (courier_id, seq, lat, lon, accuracy_m, recorded_at, received_at) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var point in batch)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync(point.CourierId, NpgsqlDbType.Uuid, cancellationToken);
                await import.WriteAsync(point.Seq, NpgsqlDbType.Bigint, cancellationToken);
                await import.WriteAsync(point.Lat, NpgsqlDbType.Double, cancellationToken);
                await import.WriteAsync(point.Lon, NpgsqlDbType.Double, cancellationToken);
                if (point.AccuracyMeters is { } accuracy)
                {
                    await import.WriteAsync(accuracy, NpgsqlDbType.Real, cancellationToken);
                }
                else
                {
                    await import.WriteNullAsync(cancellationToken);
                }

                await import.WriteAsync(point.RecordedAt.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await import.WriteAsync(point.ReceivedAt.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        int inserted;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO location_points (courier_id, seq, lat, lon, accuracy_m, recorded_at, received_at)
            SELECT courier_id, seq, lat, lon, accuracy_m, recorded_at, received_at FROM location_points_staging
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clear = new NpgsqlCommand("DELETE FROM location_points_staging", connection, transaction))
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped {Count} history points after the database kept failing")]
    private static partial void LogDropped(ILogger logger, Exception exception, int count);
}
