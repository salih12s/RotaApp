using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Primitives;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Shifts;

/// <summary>What a courier (or dispatcher) sees about a courier.</summary>
public sealed record CourierResponse(Guid Id, string DisplayName, string State, Guid? CurrentOrderId, DateTimeOffset? ShiftStartedAt)
{
    public static CourierResponse From(Courier courier)
    {
        ArgumentNullException.ThrowIfNull(courier);
        return new CourierResponse(courier.Id, courier.DisplayName, courier.State.ToString(), courier.CurrentOrderId, courier.ShiftStartedAt);
    }
}

/// <summary>
/// Shift start and end. Both are conditional updates on the courier's state; when the update changes nothing, the
/// domain object is asked why (it owns the error codes), so the HTTP answer matches the unit-tested rules.
/// </summary>
public sealed class ShiftService(DispatchDbContext db, TimeProvider time)
{
    public async Task<Result<CourierResponse>> StartAsync(Guid courierId, string? displayName, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var name = string.IsNullOrWhiteSpace(displayName) ? $"Courier {courierId.ToString("N")[..8]}" : displayName.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Until the identity provider supplies profiles (F08) a courier registers itself on the first shift.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO couriers (id, display_name, state, version, updated_at)
            VALUES ({courierId}, {name}, 'Offline', 0, {now})
            ON CONFLICT (id) DO NOTHING
            """, cancellationToken);

        var started = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Available', shift_started_at = {now}, version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Offline'
            """, cancellationToken);
        if (started == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await WhyRejectedAsync(courierId, courier => courier.StartShift(now), cancellationToken);
        }

        return await CommitAsync(courierId, now, transaction, cancellationToken);
    }

    public async Task<Result<CourierResponse>> EndAsync(Guid courierId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ended = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE couriers
            SET state = 'Offline', shift_started_at = NULL, version = version + 1, updated_at = {now}
            WHERE id = {courierId} AND state = 'Available'
            """, cancellationToken);
        if (ended == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await WhyRejectedAsync(courierId, courier => courier.EndShift(now), cancellationToken);
        }

        return await CommitAsync(courierId, now, transaction, cancellationToken);
    }

    private async Task<Result<CourierResponse>> CommitAsync(
        Guid courierId, DateTimeOffset now, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        await db.AddCourierStateChangedAsync(courierId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CourierResponse.From(await db.Couriers.AsNoTracking().SingleAsync(c => c.Id == courierId, cancellationToken));
    }

    private async Task<Result<CourierResponse>> WhyRejectedAsync(
        Guid courierId, Func<Courier, Result> action, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == courierId, cancellationToken);
        if (courier is null)
        {
            // Ending a shift that never existed: the courier is, in effect, offline.
            return DispatchErrors.InvalidCourierTransition(CourierState.Offline, CourierAction.EndShift);
        }

        var verdict = action(courier);

        // The domain would have allowed it, so the row changed between our UPDATE and this read.
        return verdict.IsFailure ? verdict.Error : DispatchErrors.ConcurrentChange;
    }
}
