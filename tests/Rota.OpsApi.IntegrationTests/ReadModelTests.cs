using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.Features.Consumers;
using Rota.OpsApi.Infrastructure;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// The read model is built from events that can repeat, overtake each other or arrive late (docs/04 §Ops API). Each test uses its own
/// order or courier id, so tests do not see each other's rows.
/// </summary>
public sealed class ReadModelTests(OpsFixture fixture)
{
    private static readonly DateTimeOffset T0 = TestClock.Start;

    private static EventEnvelope<T> Envelope<T>(T data, Guid aggregate, int version) =>
        new(Guid.CreateVersion7(), "test", T0, aggregate, version, data);

    private static OrderCreatedV1 Created(Guid orderId, int version = 1) => new(
        orderId, "merchant-ops-test", "PZ-1", new LocationV1(40.99, 29.02, "Kadıköy"), new LocationV1(40.98, 29.03, "Moda"), 1200, T0, version);

    private static OrderStatusChangedV1 Changed(Guid orderId, string to, int version, Guid? courierId = null, int minutes = 0) => new(
        orderId, "merchant-ops-test", "PZ-1", FromStatus: null, to, courierId, Reason: null, T0.AddMinutes(minutes), version);

    private async Task CreatedAsync(Guid orderId, int version = 1)
    {
        await using var db = fixture.CreateDbContext();
        await new OrderCreatedHandler(db).HandleAsync(Envelope(Created(orderId, version), orderId, version), TestContext.Current.CancellationToken);
    }

    private async Task ChangedAsync(Guid orderId, string to, int version, Guid? courierId = null, int minutes = 0)
    {
        await using var db = fixture.CreateDbContext();
        await new OrderStatusChangedHandler(db).HandleAsync(
            Envelope(Changed(orderId, to, version, courierId, minutes), orderId, version), TestContext.Current.CancellationToken);
    }

    private async Task<OrderView> OrderAsync(Guid orderId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderCreated_adds_a_row_without_any_customer_data()
    {
        var id = Guid.CreateVersion7();

        await CreatedAsync(id);

        var row = await OrderAsync(id);
        row.Status.ShouldBe(OrderStatus.Created);
        row.MerchantId.ShouldBe("merchant-ops-test");
        row.PickupLat.ShouldBe(40.99);
        row.OrderVersion.ShouldBe(1);
        row.CourierId.ShouldBeNull();
    }

    [Fact]
    public async Task The_same_OrderCreated_twice_leaves_one_unchanged_row()
    {
        var id = Guid.CreateVersion7();
        await CreatedAsync(id);
        await ChangedAsync(id, "Dispatching", version: 2);

        await CreatedAsync(id); // redelivery after the order already moved on

        var row = await OrderAsync(id);
        row.Status.ShouldBe(OrderStatus.Dispatching);
        row.OrderVersion.ShouldBe(2);
    }

    [Fact]
    public async Task A_newer_status_moves_the_row_and_sets_the_milestone_time()
    {
        var id = Guid.CreateVersion7();
        var courier = Guid.CreateVersion7();
        await CreatedAsync(id);

        await ChangedAsync(id, "Assigned", version: 3, courier, minutes: 4);
        await ChangedAsync(id, "PickedUp", version: 4, courier, minutes: 9);
        await ChangedAsync(id, "Delivered", version: 5, courier, minutes: 20);

        var row = await OrderAsync(id);
        row.Status.ShouldBe(OrderStatus.Delivered);
        row.CourierId.ShouldBe(courier);
        row.AssignedAt.ShouldBe(T0.AddMinutes(4));
        row.PickedUpAt.ShouldBe(T0.AddMinutes(9));
        row.DeliveredAt.ShouldBe(T0.AddMinutes(20));
        row.OrderVersion.ShouldBe(5);
    }

    [Fact]
    public async Task An_old_event_arriving_late_does_not_roll_the_read_model_back()
    {
        var id = Guid.CreateVersion7();
        await CreatedAsync(id);
        await ChangedAsync(id, "Delivered", version: 5, Guid.CreateVersion7(), minutes: 20);

        await ChangedAsync(id, "Assigned", version: 3, Guid.CreateVersion7(), minutes: 4); // older, delivered late

        var row = await OrderAsync(id);
        row.Status.ShouldBe(OrderStatus.Delivered);
        row.OrderVersion.ShouldBe(5);
        row.AssignedAt.ShouldBeNull("the stale event must not even leave its timestamp behind");
    }

    [Fact]
    public async Task The_same_status_event_twice_is_applied_once()
    {
        var id = Guid.CreateVersion7();
        await CreatedAsync(id);
        await ChangedAsync(id, "Assigned", version: 3, Guid.CreateVersion7(), minutes: 4);

        await ChangedAsync(id, "Assigned", version: 3, Guid.CreateVersion7(), minutes: 99);

        (await OrderAsync(id)).AssignedAt.ShouldBe(T0.AddMinutes(4));
    }

    [Fact]
    public async Task A_status_change_that_overtakes_OrderCreated_fails_so_it_is_retried_not_lost()
    {
        var id = Guid.CreateVersion7();

        await Should.ThrowAsync<OrderViewNotReadyException>(() => ChangedAsync(id, "Dispatching", version: 2));

        await CreatedAsync(id); // the retry comes after OrderCreated has landed
        await ChangedAsync(id, "Dispatching", version: 2);
        (await OrderAsync(id)).Status.ShouldBe(OrderStatus.Dispatching);
    }

    [Fact]
    public async Task CourierStateChanged_upserts_and_moves_forward_only()
    {
        var courier = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();

        async Task SendAsync(string state, Guid? current, int version, string name = "Ali K.")
        {
            await using var db = fixture.CreateDbContext();
            var data = new CourierStateChangedV1(courier, name, state, current, T0.AddMinutes(version), version);
            await new CourierStateChangedHandler(db).HandleAsync(Envelope(data, courier, version), TestContext.Current.CancellationToken);
        }

        await SendAsync("Available", null, version: 1);
        await SendAsync("OnDelivery", order, version: 3);
        await SendAsync("Offered", null, version: 2, name: "Old Name"); // older: ignored

        await using var read = fixture.CreateDbContext();
        var row = await read.Couriers.AsNoTracking().SingleAsync(c => c.CourierId == courier, TestContext.Current.CancellationToken);
        row.State.ShouldBe(CourierState.OnDelivery);
        row.CurrentOrder.ShouldBe(order);
        row.DisplayName.ShouldBe("Ali K.");
        row.CourierVersion.ShouldBe(3);
    }
}
