using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.CancelOrder;

public sealed record CancelOrderRequest([StringLength(300)] string? Reason);

/// <summary>
/// <c>POST /api/v1/orders/{id}/cancel</c>. The status check runs in the domain on the loaded row;
/// what makes it safe under concurrency is the xmin condition on the UPDATE: if someone changed
/// the order after we read it, the update hits 0 rows and we answer 409 instead of overwriting.
/// </summary>
public static class CancelOrderEndpoint
{
    public static RouteGroupBuilder MapCancelOrder(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/cancel", HandleAsync)
            .RequireAuthorization(RotaPolicies.MerchantOrDispatcher)
            .WithName("CancelOrder")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return group;
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelOrderRequest? request,
        ClaimsPrincipal user,
        OrdersDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.VisibleTo(user).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return ProblemResults.NotFound();
        }

        var now = time.GetUtcNow();
        var from = order.Status;
        var cancelled = order.Cancel(user.ToActor(), request?.Reason, now);
        if (cancelled.IsFailure)
        {
            return cancelled.Error.ToProblem();
        }

        // The event rides in the same SaveChanges (one transaction) as the status change.
        db.AddToOutbox(
            new OrderStatusChangedV1(order.Id, order.MerchantId, order.MerchantOrderRef, from.ToString(), order.Status.ToString(),
                order.AssignedCourierId, request?.Reason, now, order.Version),
            order.Id, order.Version, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new Error(ErrorCatalog.ConcurrencyConflict, "The order was changed by another request; reload it and retry.")
                .ToProblem();
        }

        return TypedResults.Ok(OrderResponse.From(order));
    }
}
