using Rota.Orders.Api.Features.CancelOrder;
using Rota.Orders.Api.Features.CreateOrder;
using Rota.Orders.Api.Features.Delivery;
using Rota.Orders.Api.Features.GetOrder;
using Rota.Orders.Api.Features.ListOrders;
using Rota.Orders.Api.Infrastructure;

namespace Rota.Orders.Api.Features;

public static class OrdersEndpoints
{
    public const string JwksPath = "/.well-known/tracking-jwks";

    public static WebApplication MapOrdersEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Every order endpoint needs a user; each one narrows the roles further.
        var orders = app.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireAuthorization();

        orders.MapCreateOrder();
        orders.MapGetOrder();
        orders.MapListOrders();
        orders.MapCancelOrder();
        orders.MapDeliveryEndpoints();

        // Public keys only. Internal network in production (docs/06 §1); no secret is exposed here.
        app.MapGet(JwksPath, (TrackingTokenIssuer issuer) => TypedResults.Ok(new { keys = new[] { issuer.PublicJwk } }))
            .AllowAnonymous()
            .WithTags("Tracking")
            .WithName("TrackingJwks");

        return app;
    }
}
