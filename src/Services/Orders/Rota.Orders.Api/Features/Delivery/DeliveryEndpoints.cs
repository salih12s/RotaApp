using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.Delivery;

public sealed record DeliverRequest(string? Code);

public sealed record DeliveryFailedRequest([StringLength(300)] string? Reason);

/// <summary>
/// The courier's side of an order (docs/05 §1 "Orders"): <c>pickup</c>, <c>deliver</c>, <c>delivery-failed</c>, <c>returned</c>,
/// and the dispatcher's <c>delivery-code/unlock</c>. Which orders a caller can touch is part of the query
/// (<see cref="OrderAccess.VisibleTo"/>): a courier only ever finds the orders assigned to them, so someone else's order is a 404,
/// exactly like one that does not exist. What makes a status change safe under concurrency is the <c>xmin</c> condition on the
/// UPDATE (as in cancel): if anything changed the order after we read it, the UPDATE hits 0 rows and the answer is 409.
/// </summary>
public static class DeliveryEndpoints
{
    public static RouteGroupBuilder MapDeliveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/pickup", PickUpAsync)
            .RequireAuthorization(RotaPolicies.CourierOnly).WithName("PickUpOrder");
        group.MapPost("/{id:guid}/deliver", DeliverAsync)
            .RequireAuthorization(RotaPolicies.CourierOnly).WithName("DeliverOrder");
        group.MapPost("/{id:guid}/delivery-failed", DeliveryFailedAsync)
            .RequireAuthorization(RotaPolicies.CourierOnly).WithName("FailDelivery");
        group.MapPost("/{id:guid}/returned", ReturnedAsync)
            .RequireAuthorization(RotaPolicies.CourierOrDispatcher).WithName("ReturnOrder");
        group.MapPost("/{id:guid}/delivery-code/unlock", UnlockAsync)
            .RequireAuthorization(RotaPolicies.DispatcherOnly).WithName("UnlockDeliveryCode");
        return group;
    }

    private static Task<IResult> PickUpAsync(Guid id, ClaimsPrincipal user, OrdersDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, user, db, time, reason: null, (order, actor, now) => order.PickUp(actor, now), cancellationToken);

    private static Task<IResult> ReturnedAsync(Guid id, ClaimsPrincipal user, OrdersDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, user, db, time, reason: null, (order, actor, now) => order.MarkReturned(actor, now), cancellationToken);

    private static Task<IResult> DeliveryFailedAsync(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeliveryFailedRequest? request,
        ClaimsPrincipal user, OrdersDbContext db, TimeProvider time, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, user, db, time, request?.Reason, (order, actor, now) => order.FailDelivery(actor, request?.Reason, now), cancellationToken);

    private static async Task<IResult> ChangeStatusAsync(
        Guid id, ClaimsPrincipal user, OrdersDbContext db, TimeProvider time, string? reason,
        Func<Order, OrderActor, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        var order = await db.Orders.VisibleTo(user).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return ProblemResults.NotFound();
        }

        var now = time.GetUtcNow();
        var from = order.Status;
        var changed = change(order, user.ToActor(), now);
        if (changed.IsFailure)
        {
            return changed.Error.ToProblem();
        }

        // Same SaveChanges (one transaction) as the status change: no change without its event, no event without its change.
        AddStatusChanged(db, order, from, reason, now);
        return await SaveAsync(db, order, cancellationToken);
    }

    private static async Task<IResult> DeliverAsync(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeliverRequest? request,
        ClaimsPrincipal user, OrdersDbContext db, DeliveryCodeHasher hasher, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.VisibleTo(user).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return ProblemResults.NotFound();
        }

        var now = time.GetUtcNow();
        var from = order.Status;
        var delivered = order.Deliver(request?.Code ?? string.Empty, hasher, user.ToActor(), now);
        if (delivered.IsSuccess)
        {
            AddStatusChanged(db, order, from, reason: null, now);
            return await SaveAsync(db, order, cancellationToken);
        }

        // A wrong code is a failure that still changed the order: the attempt counter (and maybe the lock). It has to be
        // saved, or the five-attempt limit would never trigger. The xmin condition also keeps parallel guesses honest:
        // of several simultaneous wrong codes only some are written, the rest get 409, so nobody gets extra tries.
        if (delivered.Error.Code == OrderErrors.InvalidDeliveryCode.Code)
        {
            var saved = await TrySaveAsync(db, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error.ToProblem();
            }

            return new Error(delivered.Error.Code, delivered.Error.Message,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["remainingAttempts"] = order.DeliveryCode.RemainingAttempts })
                .ToProblem();
        }

        return delivered.Error.ToProblem();
    }

    private static async Task<IResult> UnlockAsync(Guid id, ClaimsPrincipal user, OrdersDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var order = await db.Orders.VisibleTo(user).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return ProblemResults.NotFound();
        }

        var unlocked = order.UnlockDeliveryCode(time.GetUtcNow());
        if (unlocked.IsFailure)
        {
            return unlocked.Error.ToProblem();
        }

        // The status does not change, so there is no OrderStatusChanged event to write.
        return await SaveAsync(db, order, cancellationToken);
    }

    private static void AddStatusChanged(OrdersDbContext db, Order order, OrderStatus from, string? reason, DateTimeOffset now) =>
        db.AddToOutbox(
            new OrderStatusChangedV1(order.Id, order.MerchantId, order.MerchantOrderRef, from.ToString(), order.Status.ToString(),
                order.AssignedCourierId, reason, now, order.Version),
            order.Id, order.Version, now);

    private static async Task<IResult> SaveAsync(OrdersDbContext db, Order order, CancellationToken cancellationToken)
    {
        var saved = await TrySaveAsync(db, cancellationToken);
        return saved.IsFailure ? saved.Error.ToProblem() : TypedResults.Ok(OrderResponse.From(order));
    }

    private static async Task<Result> TrySaveAsync(OrdersDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return new Error(ErrorCatalog.ConcurrencyConflict, "The order was changed by another request; reload it and retry.");
        }
    }
}
