using System.Security.Claims;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Orders.Api.Domain;

namespace Rota.Orders.Api.Features;

/// <summary>
/// Who may see which order (docs/06 §2), applied inside the query: an order that is not the
/// caller's never leaves the database, so "not yours" and "does not exist" are the same 404.
/// </summary>
public static class OrderAccess
{
    public static IQueryable<Order> VisibleTo(this IQueryable<Order> orders, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.IsInRole(RotaRoles.Dispatcher))
        {
            return orders;
        }

        if (user.IsInRole(RotaRoles.Merchant) && user.GetMerchantId() is { } merchantId)
        {
            return orders.Where(o => o.MerchantId == merchantId);
        }

        if (user.GetCourierId() is { } courierId)
        {
            return orders.Where(o => o.AssignedCourierId == courierId);
        }

        return orders.Where(_ => false);
    }

    /// <summary>The actor recorded in the status history for a merchant or dispatcher action.</summary>
    public static OrderActor ToActor(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.IsInRole(RotaRoles.Merchant) && user.GetMerchantId() is { } merchantId)
        {
            return OrderActor.Merchant(merchantId);
        }

        if (user.IsInRole(RotaRoles.Dispatcher) && user.GetSubject() is { } dispatcherId)
        {
            return OrderActor.Dispatcher(dispatcherId);
        }

        if (user.GetCourierId() is { } courierId)
        {
            return OrderActor.Courier(courierId);
        }

        throw new InvalidOperationException("An authenticated principal without a known role.");
    }
}
