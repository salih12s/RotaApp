using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Infrastructure;

namespace Rota.Notifications.Api.Features.Delivery;

/// <summary>
/// One round of "send every webhook whose time has come". The worker calls it once a second; tests call it by hand, so they decide when time passes.
/// <para>
/// The same pattern as Dispatch's search and timeout jobs (ADR-003, ADR-016): due rows are taken with <c>FOR UPDATE SKIP LOCKED</c> and hidden from other
/// workers by pushing <c>next_attempt_at</c> a lease ahead; the result is then written with a conditional UPDATE.
/// </para>
/// <para>
/// A slow merchant must not slow the others (bulkhead): at most <c>MerchantConcurrency</c> requests are in flight to one merchant, further rows of that
/// merchant are handed back (not counted as attempts) and tried next round, and one round takes only a few rows per merchant so a backlog cannot
/// fill the whole batch. A merchant that keeps failing is cut off for a minute (circuit breaker).
/// </para>
/// </summary>
public sealed partial class DeliveryRounds(
    IServiceScopeFactory scopes, MerchantGates gates, BackoffSchedule schedule, IOptions<NotificationsOptions> options,
    TimeProvider time, ILogger<DeliveryRounds> logger)
{
    private const int MaxParallel = 32;
    private static readonly TimeSpan _handBack = TimeSpan.FromSeconds(1);

    /// <summary>Claims due deliveries, tries each once and waits until all are done. Returns how many it picked up. (Tests call this.)</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var (claimed, processing) = await StartRoundAsync(cancellationToken);
        await processing;
        return claimed;
    }

    /// <summary>
    /// Claims due deliveries and starts trying them, but does <b>not</b> wait for them: the caller gets the number claimed and a task that
    /// completes when they are all done. The worker uses this so a round full of slow requests (one slow merchant, 10 s each) cannot hold up
    /// the next round: the next tick claims the other merchants' rows at once. Claimed rows are hidden by their lease, so nothing is taken twice.
    /// </summary>
    public async Task<(int Claimed, Task Processing)> StartRoundAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WebhookDelivery> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            claimed = await ClaimDueAsync(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(), cancellationToken);
        }

        var processing = Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = cancellationToken },
            async (delivery, token) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    await TryDeliverAsync(scope.ServiceProvider, delivery, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad row must not stop the others; its lease runs out and it is tried again.
                    LogDeliveryFailed(logger, ex, delivery.Id);
                }
            });

        return (claimed.Count, processing);
    }

    private async Task TryDeliverAsync(IServiceProvider services, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<NotificationsDbContext>();
        var gate = gates.For(delivery.MerchantId);
        var now = time.GetUtcNow();

        if (gate.Circuit.OpenUntil(now) is { } reopenAt)
        {
            await RescheduleAsync(db, delivery.Id, reopenAt, cancellationToken);
            return;
        }

        // Do not wait for a slot: waiting would tie up a worker thread on a merchant that is already slow.
        if (!await gate.Slots.WaitAsync(0, cancellationToken))
        {
            await RescheduleAsync(db, delivery.Id, now + _handBack, cancellationToken);
            return;
        }

        try
        {
            var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.MerchantId == delivery.MerchantId, cancellationToken);
            SendResult result;
            if (subscription is not { IsActive: true })
            {
                result = new SendResult(false, null, "The merchant has no active webhook subscription.");
            }
            else
            {
                string secret;
                try
                {
                    secret = services.GetRequiredService<SecretProtector>().Unprotect(subscription.SecretProtected);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // The key that encrypted it is gone. The row keeps failing visibly instead of being signed with a wrong secret.
                    result = new SendResult(false, null, "The webhook secret cannot be decrypted (Data Protection key missing).");
                    await RecordAsync(db, delivery, result, cancellationToken);
                    return;
                }

                result = await services.GetRequiredService<WebhookSender>().SendAsync(subscription.Url, secret, delivery, cancellationToken);
            }

            var at = time.GetUtcNow();
            if (result.Success)
            {
                gate.Circuit.RecordSuccess();
            }
            else if (subscription is { IsActive: true })
            {
                gate.Circuit.RecordFailure(at);
            }

            await RecordAsync(db, delivery, result, cancellationToken);
        }
        finally
        {
            gate.Slots.Release();
        }
    }

    private async Task RecordAsync(NotificationsDbContext db, WebhookDelivery delivery, SendResult result, CancellationToken cancellationToken)
    {
        var at = time.GetUtcNow();
        var moved = result.Success
            ? delivery.MarkSucceeded(result.StatusCode ?? 200, at)
            : delivery.MarkFailed(result.StatusCode, result.Error ?? "Unknown failure.", schedule, at);
        if (moved.IsFailure)
        {
            return;
        }

        // Conditional on "still pending": a replay or a second worker cannot be overwritten.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE webhook_deliveries
            SET status = {delivery.Status.ToString()}, attempt_count = {delivery.AttemptCount}, next_attempt_at = {delivery.NextAttemptAt},
                last_status_code = {delivery.LastStatusCode}, last_error = {delivery.LastError}, delivered_at = {delivery.DeliveredAt}
            WHERE id = {delivery.Id} AND status = 'Pending'
            """, cancellationToken);
    }

    private static Task<int> RescheduleAsync(NotificationsDbContext db, Guid id, DateTimeOffset when, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE webhook_deliveries SET next_attempt_at = {when} WHERE id = {id} AND status = 'Pending'", cancellationToken);

    private async Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(NotificationsDbContext db, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var leaseUntil = now + settings.Lease;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Step 1: which rows? At most PerMerchantClaim per merchant, oldest first. (Window functions and row locks cannot share a query.)
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM (
                SELECT id, next_attempt_at, row_number() OVER (PARTITION BY merchant_id ORDER BY next_attempt_at, id) AS rn
                FROM webhook_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}) due
            WHERE rn <= {settings.PerMerchantClaim}
            ORDER BY next_attempt_at, id
            LIMIT {settings.WorkerBatchSize}
            """).ToListAsync(cancellationToken);

        // Step 2: lock them; a row another worker has locked is skipped, not waited for.
        var rows = ids.Count == 0
            ? []
            : await db.Deliveries.FromSql($"""
                    SELECT * FROM webhook_deliveries
                    WHERE id = ANY({ids.ToArray()}) AND status = 'Pending' AND next_attempt_at <= {now}
                    FOR UPDATE SKIP LOCKED
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

        if (rows.Count > 0)
        {
            var locked = rows.Select(r => r.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE webhook_deliveries SET next_attempt_at = {leaseUntil} WHERE id = ANY({locked})", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} failed unexpectedly; it will be tried again when its lease ends")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, Guid deliveryId);
}

/// <summary>Runs <see cref="DeliveryRounds"/> once a second on a real timer (infrastructure time, docs/07 §3).</summary>
public sealed partial class DeliveryWorker(DeliveryRounds rounds, IOptions<NotificationsOptions> options, ILogger<DeliveryWorker> logger) : BackgroundService
{
    // Rows being worked on right now. Past this many the worker stops claiming until some finish, so a flood of slow merchants cannot pile up without bound.
    private const int MaxInFlight = 256;

    private int _inFlight;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.WorkerPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!options.Value.WorkerEnabled || Volatile.Read(ref _inFlight) >= MaxInFlight)
                {
                    continue;
                }

                try
                {
                    // Not awaited: see DeliveryRounds.StartRoundAsync.
                    var (claimed, processing) = await rounds.StartRoundAsync(stoppingToken);
                    if (claimed > 0)
                    {
                        Interlocked.Add(ref _inFlight, claimed);
                        _ = processing.ContinueWith(_ => Interlocked.Add(ref _inFlight, -claimed), TaskScheduler.Default);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRoundFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A webhook delivery round failed; trying again on the next tick")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
