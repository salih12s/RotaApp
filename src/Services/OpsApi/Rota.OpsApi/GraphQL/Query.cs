using HotChocolate.Authorization;
using HotChocolate.Data.Sorting;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Web.Auth;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

/// <summary>
/// The dispatcher's read side (docs/05 §5). Every field needs the <c>dispatcher</c> role. The order list uses the request's own
/// <see cref="OpsDbContext"/> (its query runs when the page is built, after the resolver returns); every other field opens a short-lived one,
/// because root fields of one request run in parallel and one DbContext may not serve two queries at once.
/// </summary>
public sealed class Query
{
    /// <summary>Filter, sort and cursor-page over the order list. Newest first unless the caller sorts otherwise; the id breaks ties so pages never overlap.</summary>
    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    [UsePaging(DefaultPageSize = 50, MaxPageSize = 200, IncludeTotalCount = false)]
    [UseFiltering(typeof(OrderFilterInputType))]
    [UseSorting(typeof(OrderSortInputType))]
    public IQueryable<OrderView> GetOrders([Service] OpsDbContext db, ISortingContext sorting)
    {
        var orders = db.Orders.AsNoTracking();
        if (!sorting.IsDefined)
        {
            return orders.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.OrderId);
        }

        // The caller's sort may tie (many orders share a status); the id after it keeps every page boundary exact.
        sorting.OnAfterSortingApplied<IOrderedQueryable<OrderView>>((_, query) => query.ThenBy(o => o.OrderId));
        return orders;
    }

    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    public async Task<OrderView?> GetOrderAsync(Guid id, [Service] IDbContextFactory<OpsDbContext> factory, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == id, cancellationToken);
    }

    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    public async Task<IReadOnlyList<CourierView>> GetCouriersAsync(
        CourierState? state, [Service] IDbContextFactory<OpsDbContext> factory, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var couriers = db.Couriers.AsNoTracking();
        if (state is { } wanted)
        {
            couriers = couriers.Where(c => c.State == wanted);
        }

        return await couriers.OrderBy(c => c.DisplayName).ThenBy(c => c.CourierId).ToListAsync(cancellationToken);
    }

    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    public async Task<DispatchOverview> GetDispatchOverviewAsync([Service] IDbContextFactory<OpsDbContext> factory, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Two grouped queries instead of four counts.
        var orders = await db.Orders.AsNoTracking().GroupBy(o => o.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var available = await db.Couriers.AsNoTracking().CountAsync(c => c.State == CourierState.Available, cancellationToken);

        int Count(params OrderStatus[] statuses) => orders.Where(o => statuses.Contains(o.Status)).Sum(o => o.Count);

        return new DispatchOverview(
            CreatedOrders: Count(OrderStatus.Created),
            UnassignedOrders: Count(OrderStatus.Unassigned),
            ActiveDeliveries: Count(OrderStatus.Assigned, OrderStatus.PickedUp),
            AvailableCouriers: available);
    }
}

public sealed record DispatchOverview(int CreatedOrders, int UnassignedOrders, int ActiveDeliveries, int AvailableCouriers);

public sealed record GeoPoint(double Lat, double Lon);
