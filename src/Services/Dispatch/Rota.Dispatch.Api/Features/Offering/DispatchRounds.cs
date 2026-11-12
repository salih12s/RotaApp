using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Offering;

/// <summary>
/// One round of the two background jobs: "find a courier for every due job" and "expire every due offer".
/// <see cref="DispatchWorker"/> calls it on a timer; tests call it by hand, so they decide when time passes.
/// </summary>
public sealed partial class DispatchRounds(
    IServiceScopeFactory scopes, IOptions<DispatchOptions> options, TimeProvider time, ILogger<DispatchRounds> logger)
{
    private const int MaxParallelSearches = 8;

    /// <summary>Claims due jobs and runs one search step for each. Returns how many jobs it picked up.</summary>
    public async Task<int> SearchOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claimed = await ClaimDueJobsAsync(scope.ServiceProvider.GetRequiredService<DispatchDbContext>(), cancellationToken);
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSearches, CancellationToken = cancellationToken },
            async (orderId, token) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<OfferingService>().TryOfferAsync(orderId, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad job must not stop the others; its lease runs out and it is tried again.
                    LogSearchFailed(logger, ex, orderId);
                }
            });

        return claimed.Count;
    }

    /// <summary>Expires due offers. Returns how many were expired.</summary>
    public async Task<int> ExpireOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OfferTimeoutProcessor>()
            .ProcessDueAsync(options.Value.WorkerBatchSize, cancellationToken);
    }

    /// <summary>
    /// Takes jobs that are due (<c>next_search_at</c> empty or passed) and hides them from other workers by pushing
    /// <c>next_search_at</c> a lease ahead. <c>SKIP LOCKED</c> lets several workers claim at the same moment without
    /// waiting for or duplicating each other. If this worker dies, the lease expires and the job is due again.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ClaimDueJobsAsync(DispatchDbContext db, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var leaseUntil = now + settings.SearchLease;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var due = await db.DispatchJobs.FromSql($"""
                SELECT * FROM dispatch_jobs
                WHERE status = 'Searching' AND (next_search_at IS NULL OR next_search_at <= {now})
                ORDER BY search_started_at
                LIMIT {settings.WorkerBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var ids = due.Select(j => j.OrderId).ToArray();
        if (ids.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dispatch_jobs SET next_search_at = {leaseUntil} WHERE order_id = ANY({ids})", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Search step for order {OrderId} failed; it will be retried when its lease ends")]
    private static partial void LogSearchFailed(ILogger logger, Exception exception, Guid orderId);
}

/// <summary>Runs <see cref="DispatchRounds"/> once a second on a real timer (infrastructure time, docs/07 §3).</summary>
public sealed partial class DispatchWorker(DispatchRounds rounds, IOptions<DispatchOptions> options, ILogger<DispatchWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.WorkerPollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!options.Value.WorkersEnabled)
                {
                    continue;
                }

                try
                {
                    await rounds.ExpireOnceAsync(stoppingToken);
                    await rounds.SearchOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The database may be briefly down; the next tick tries again.
                    LogRoundFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A dispatch worker round failed; trying again on the next tick")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
