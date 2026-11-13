using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.Features.Consumers;
using Rota.OpsApi.GraphQL;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>Puts orders into the read model the way production does: by running the real consumers.</summary>
public static class Seed
{
    private static readonly DateTimeOffset T0 = TestClock.Start;

    /// <summary>A new order moved to <paramref name="status"/> (version 2). Returns its id and the courier it was given.</summary>
    public static async Task<(Guid OrderId, Guid CourierId)> OrderAsync(OpsFixture fixture, string status, CancellationToken cancellationToken)
    {
        var orderId = Guid.CreateVersion7();
        var courierId = Guid.CreateVersion7();
        await using var db = fixture.CreateDbContext();

        var created = new OrderCreatedV1(orderId, "merchant-seed", "PZ-1", new LocationV1(40.99, 29.02, "A"), new LocationV1(40.98, 29.03, "B"), 900, T0, 1);
        await new OrderCreatedHandler(db).HandleAsync(new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), "t", T0, orderId, 1, created), cancellationToken);
        await ChangeAsync(db, orderId, status, courierId, version: 2, notifier: null, cancellationToken);
        return (orderId, courierId);
    }

    /// <summary>The courier row for a courier who is on delivery of <paramref name="orderId"/>.</summary>
    public static async Task CourierAsync(OpsFixture fixture, Guid courierId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var db = fixture.CreateDbContext();
        var data = new CourierStateChangedV1(courierId, "Kurye Test", "OnDelivery", orderId, T0, 1);
        await new CourierStateChangedHandler(db).HandleAsync(new EventEnvelope<CourierStateChangedV1>(Guid.CreateVersion7(), "t", T0, courierId, 1, data), cancellationToken);
    }

    /// <summary>Applies one <c>OrderStatusChanged</c> through the real handler; with a <paramref name="notifier"/> it also tells live subscribers.</summary>
    public static async Task ChangeAsync(
        Rota.OpsApi.Infrastructure.OpsDbContext db, Guid orderId, string to, Guid? courierId, int version, OrderStatusNotifier? notifier, CancellationToken cancellationToken)
    {
        var at = T0.AddMinutes(version);
        var change = new OrderStatusChangedV1(orderId, "merchant-seed", "PZ-1", null, to, courierId, null, at, version);
        await new OrderStatusChangedHandler(db, notifier).HandleAsync(new EventEnvelope<OrderStatusChangedV1>(Guid.CreateVersion7(), "t", at, orderId, version, change), cancellationToken);
    }
}
