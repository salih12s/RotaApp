using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Primitives;
using Rota.Contracts.Events.Dispatch;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Jobs;

public sealed record JobResponse(Guid OrderId, string Status, Guid? CourierId);

/// <summary>Dispatcher commands on a job: assign a courier by hand, or restart a job that gave up.</summary>
public sealed class JobCommandService(DispatchDbContext db, TimeProvider time)
{
    /// <summary>
    /// Manual assignment skips the offer: the job completes and the courier goes straight to <c>OnDelivery</c>, in one
    /// transaction. The courier's own conditional update (<c>WHERE state = 'Available'</c>) decides; a courier who is
    /// busy, offline or unknown is a 409/404, never a second job on the same courier (invariant I-1).
    /// </summary>
    public async Task<Result<JobResponse>> AssignManuallyAsync(Guid orderId, Guid courierId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock order: job first, then courier.
        var assigned = await db.TransitionJobAsync(orderId, j => j.ManualAssign(now), cancellationToken);
        if (assigned.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ExplainJobRefusalAsync(orderId, assigned.Error, cancellationToken);
        }

        if (!await db.TryAssignCourierManuallyAsync(courierId, orderId, now, cancellationToken))
        {
            // Rolling back also undoes the job's move to Completed.
            await transaction.RollbackAsync(cancellationToken);
            var courier = await db.Couriers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == courierId, cancellationToken);
            return courier is null
                ? DispatchErrors.NotFound("courier")
                : DispatchErrors.CourierNotAvailable(courier.State);
        }

        await db.AddCourierStateChangedAsync(courierId, now, cancellationToken);

        // The contract has no "unknown" ETA; a manual assignment carries 0 (no route was computed).
        db.AddToOutbox(new CourierAssignedV1(orderId, courierId, OfferId: null, "Manual", EtaSeconds: 0, now),
            orderId, aggregateVersion: 1, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new JobResponse(orderId, DispatchJobStatus.Completed.ToString(), courierId);
    }

    /// <summary>A job that ended <c>Unassigned</c> searches again with a fresh attempt budget; Orders hears <c>DispatchStarted</c>.</summary>
    public async Task<Result<JobResponse>> RestartAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var restarted = await db.TransitionJobAsync(orderId, j => j.Restart(now), cancellationToken);
        if (restarted.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ExplainJobRefusalAsync(orderId, restarted.Error, cancellationToken);
        }

        db.AddToOutbox(new DispatchStartedV1(orderId, now), orderId, aggregateVersion: 1, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new JobResponse(orderId, DispatchJobStatus.Searching.ToString(), CourierId: null);
    }

    // The domain says "invalid-job-transition", which is a Dispatch-internal code; the API answers with the
    // catalog's "job-not-assignable" and the job's current status. A real race stays a concurrency conflict.
    private async Task<Result<JobResponse>> ExplainJobRefusalAsync(Guid orderId, Error error, CancellationToken cancellationToken)
    {
        if (error.Code is "not-found" or "concurrency-conflict")
        {
            return error;
        }

        var job = await db.DispatchJobs.AsNoTracking().SingleAsync(j => j.OrderId == orderId, cancellationToken);
        return DispatchErrors.JobNotAssignable(job.Status);
    }
}
