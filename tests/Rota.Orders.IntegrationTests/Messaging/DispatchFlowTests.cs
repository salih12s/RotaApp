extern alias DispatchApi;

using System.Net;
using System.Net.Http.Json;
using DispatchApi::Rota.Dispatch.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Orders.Api.Domain;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>
/// F05 across both services and a real RabbitMQ: what Dispatch decides reaches Orders, and what Orders decides reaches Dispatch.
/// Integration tests (not unit tests) because the point is the hand-over: routing keys, queues, the inbox and two databases.
/// Dispatch's own search needs Tracking and is covered in Rota.Dispatch.IntegrationTests; here a dispatcher assigns by hand.
/// </summary>
[Trait("Category", "Messaging")]
public sealed class DispatchFlowTests(OrdersDatabaseFixture databases, BrokerFixture brokers)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManualAssignment_InDispatch_MakesTheOrderAssignedInOrders()
    {
        await using var scenario = await StartBothAsync();
        var (orderId, courierId, _, _) = await CreateOrderAndAssignAsync(scenario);

        await using var db = scenario.OrdersDb();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Token);
        order.Status.ShouldBe(OrderStatus.Assigned);
        order.AssignedCourierId.ShouldBe(courierId);
    }

    [Fact]
    public async Task CancellingTheOrder_InOrders_FreesTheCourierInDispatch()
    {
        await using var scenario = await StartBothAsync();
        var (orderId, courierId, merchantId, _) = await CreateOrderAndAssignAsync(scenario);

        using var merchant = scenario.Orders!.CreateClientAs(DevUsers.Merchant(merchantId));
        var cancel = await merchant.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "customer changed mind" }, Token);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.UntilAsync(async () =>
        {
            await using var db = scenario.DispatchDb();
            return await db.Couriers.AsNoTracking().Where(c => c.Id == courierId).Select(c => c.State).SingleAsync(Token) == CourierState.Available;
        }, _wait, "courier is Available again after the order was cancelled", Token);
        await using var check = scenario.DispatchDb();
        (await check.Couriers.AsNoTracking().SingleAsync(c => c.Id == courierId, Token)).CurrentOrderId.ShouldBeNull();
    }

    [Fact]
    public async Task Delivery_InOrders_FreesTheCourierInDispatchWithinTwoSeconds()
    {
        // F06 PASS 4: order Delivered -> OrderStatusChanged -> Dispatch consumer -> courier Available.
        await using var scenario = await StartBothAsync();
        var (orderId, courierId, _, code) = await CreateOrderAndAssignAsync(scenario);
        using var courier = scenario.Orders!.CreateClientAs(DevUsers.Courier(courierId));
        (await courier.PostAsync($"/api/v1/orders/{orderId}/pickup", content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CourierStateAsync(scenario, courierId)).ShouldBe(CourierState.OnDelivery, "still busy until delivered");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var delivered = await courier.PostAsJsonAsync($"/api/v1/orders/{orderId}/deliver", new { code }, Token);
        delivered.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.UntilAsync(async () => await CourierStateAsync(scenario, courierId) == CourierState.Available,
            TimeSpan.FromSeconds(2), "courier is Available within 2 s of the delivery", Token);
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task FailedDeliveryAndReturn_FreeTheCourierOnlyWhenTheParcelIsReturned()
    {
        await using var scenario = await StartBothAsync();
        var (orderId, courierId, _, _) = await CreateOrderAndAssignAsync(scenario);
        using var courier = scenario.Orders!.CreateClientAs(DevUsers.Courier(courierId));
        await courier.PostAsync($"/api/v1/orders/{orderId}/pickup", content: null, Token);
        (await courier.PostAsJsonAsync($"/api/v1/orders/{orderId}/delivery-failed", new { reason = "customer absent" }, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForOrderAsync(scenario, orderId, OrderStatus.DeliveryFailed);
        (await CourierStateAsync(scenario, courierId)).ShouldBe(CourierState.OnDelivery, "the parcel is still with the courier");

        (await courier.PostAsync($"/api/v1/orders/{orderId}/returned", content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.UntilAsync(async () => await CourierStateAsync(scenario, courierId) == CourierState.Available,
            TimeSpan.FromSeconds(5), "courier is Available after the parcel was returned", Token);
    }

    [Fact]
    public async Task DispatchFailed_MovesTheOrderToUnassigned()
    {
        await using var scenario = await StartBothAsync();
        var orderId = await scenario.CreateOrderAsync(DevUsers.NewMerchantId(), null, Token);
        await WaitForOrderAsync(scenario, orderId, OrderStatus.Dispatching);

        await scenario.Probe.PublishEventAsync(new DispatchFailedV1(orderId, "NoCandidates", TestClock.Start), Guid.CreateVersion7(), orderId, 1, Token);

        await WaitForOrderAsync(scenario, orderId, OrderStatus.Unassigned);
        await using var db = scenario.OrdersDb();
        var changes = await db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == "OrderStatusChanged.v1").ToListAsync(Token);
        changes.ShouldContain(m => m.Payload.Contains("Unassigned") && m.Payload.Contains("NoCandidates"));
    }

    [Fact]
    public async Task CourierAssigned_ForAnOrderCancelledMeanwhile_IsAcknowledgedAndIgnored()
    {
        // The race the docs describe (05 §3): Dispatch assigned while the merchant cancelled. Orders keeps the cancellation;
        // the message must not be retried into the DLQ (nothing would ever make the transition legal), and Dispatch frees the courier.
        await using var scenario = await StartBothAsync();
        var merchantId = DevUsers.NewMerchantId();
        var orderId = await scenario.CreateOrderAsync(merchantId, null, Token);
        await WaitForOrderAsync(scenario, orderId, OrderStatus.Dispatching);
        using var merchant = scenario.Orders!.CreateClientAs(DevUsers.Merchant(merchantId));
        (await merchant.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "changed mind" }, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var eventId = Guid.CreateVersion7();
        await scenario.Probe.PublishEventAsync(
            new CourierAssignedV1(orderId, Guid.CreateVersion7(), OfferId: null, "Auto", 120, TestClock.Start), eventId, orderId, 1, Token);

        await Eventually.UntilAsync(async () =>
        {
            await using var db = scenario.OrdersDb();
            return await db.Set<InboxMessage>().AnyAsync(m => m.MessageId == eventId, Token);
        }, _wait, "Orders processed (and acknowledged) the CourierAssigned event", Token);

        (await scenario.OrderStatusAsync(orderId, Token)).ShouldBe(OrderStatus.Cancelled);
        (await scenario.Probe.MessageCountAsync(Topology.DeadLetterQueue("q.orders.courier-assigned"), Token)).ShouldBe(0u);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private async Task<MessagingScenario> StartBothAsync() =>
        await MessagingScenario.StartAsync(databases, await brokers.GetAsync(), startOrders: true, startDispatch: true, ScenarioTimings.Fast, Token);

    /// <summary>Order created → Dispatch has a job → a courier starts a shift → a dispatcher assigns them → Orders says Assigned.</summary>
    private static async Task<(Guid OrderId, Guid CourierId, string MerchantId, string DeliveryCode)> CreateOrderAndAssignAsync(MessagingScenario scenario)
    {
        var merchantId = DevUsers.NewMerchantId();
        var (orderId, code) = await scenario.CreateOrderWithCodeAsync(merchantId, null, Token);
        await WaitForOrderAsync(scenario, orderId, OrderStatus.Dispatching); // proves OrderCreated reached Dispatch and DispatchStarted came back

        var courierId = Guid.CreateVersion7();
        using var courier = scenario.Dispatch!.CreateClient().As(DevUsers.Courier(courierId));
        (await courier.PostAsync("/api/v1/couriers/me/shift/start", content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var dispatcher = scenario.Dispatch!.CreateClient().As(DevUsers.Dispatcher("dsp-1"));
        var assign = await dispatcher.PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Token);
        assign.StatusCode.ShouldBe(HttpStatusCode.OK, await assign.Content.ReadAsStringAsync(Token));

        await WaitForOrderAsync(scenario, orderId, OrderStatus.Assigned);
        return (orderId, courierId, merchantId, code);
    }

    private static async Task<CourierState> CourierStateAsync(MessagingScenario scenario, Guid courierId)
    {
        await using var db = scenario.DispatchDb();
        return await db.Couriers.AsNoTracking().Where(c => c.Id == courierId).Select(c => c.State).SingleAsync(Token);
    }

    private static Task WaitForOrderAsync(MessagingScenario scenario, Guid orderId, OrderStatus expected) =>
        Eventually.UntilAsync(async () => await scenario.OrderStatusAsync(orderId, Token) == expected, _wait, $"order {orderId} is {expected}", Token);
}
