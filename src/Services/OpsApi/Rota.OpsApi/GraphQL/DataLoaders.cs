using Microsoft.EntityFrameworkCore;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

// A DataLoader collects every "give me X for this id" asked while one level of the response is built, and answers them with ONE query.
// Without it, 50 orders asking for their courier would be 50 queries (the N+1 problem). One instance lives for one request.

/// <summary><c>Order.courier</c>: couriers from <c>courier_view</c> by id, in one query for the whole batch.</summary>
public sealed class CourierByIdLoader(IDbContextFactory<OpsDbContext> factory, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, CourierView>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, CourierView>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Couriers.AsNoTracking().Where(c => keys.Contains(c.CourierId)).ToDictionaryAsync(c => c.CourierId, cancellationToken);
    }
}

/// <summary><c>Courier.currentOrder</c>: orders from <c>order_view</c> by id, in one query.</summary>
public sealed class OrderByIdLoader(IDbContextFactory<OpsDbContext> factory, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, OrderView>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, OrderView>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Orders.AsNoTracking().Where(o => keys.Contains(o.OrderId)).ToDictionaryAsync(o => o.OrderId, cancellationToken);
    }
}

/// <summary><c>Courier.lastLocation</c>: one Tracking gRPC call for every courier in the response.</summary>
public sealed class LastLocationLoader(TrackingLocations tracking, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, CourierLocationInfo>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, CourierLocationInfo>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken) =>
        await tracking.GetLastAsync(keys, cancellationToken);
}
