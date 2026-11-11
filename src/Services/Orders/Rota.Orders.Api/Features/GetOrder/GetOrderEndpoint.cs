using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features.GetOrder;

public static class GetOrderEndpoint
{
    public static RouteGroupBuilder MapGetOrder(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}", HandleAsync)
            .RequireAuthorization(RotaPolicies.AnyActor)
            .WithName("GetOrder")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return group;
    }

    private static async Task<IResult> HandleAsync(Guid id, ClaimsPrincipal user, OrdersDbContext db, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .VisibleTo(user)
            .SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null ? ProblemResults.NotFound() : TypedResults.Ok(OrderResponse.From(order));
    }
}
