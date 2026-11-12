using Microsoft.Extensions.DependencyInjection;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Orders;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Consumers;
using Rota.Dispatch.Api.Infrastructure;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests.Consumers;

/// <summary>
/// What Dispatch does when Orders reports a status change. The handler is called the way the consumer service calls it: inside a
/// transaction that is committed afterwards (the inbox row and the broker are covered by the messaging tests of F03).
/// </summary>
public sealed class OrderEventConsumerTests(DispatchFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- OrderCreated --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrderCreated_StartsASearchingJobWithBothAddressesAndAnnouncesDispatchStarted()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = Guid.CreateVersion7();
        var created = new OrderCreatedV1(orderId, "merchant-x", "ref-1",
            new LocationV1(40.99, 29.02, "Moda Cd. 5, Kadıköy"), new LocationV1(41.01, 29.05, "Bağdat Cd. 7, Kadıköy"),
            DistanceMeters: 3_400, TestClock.Start, OrderVersion: 1);
        var message = new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), OrderCreatedV1.EventType, TestClock.Start, orderId, 1, created);

        await using (var scope = s.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            await scope.ServiceProvider.GetRequiredService<OrderCreatedHandler>().HandleAsync(message, Cancel);
            await db.SaveChangesAsync(Cancel);
            await transaction.CommitAsync(Cancel);
        }

        var job = await s.GetJobAsync(orderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.PickupAddress.ShouldBe("Moda Cd. 5, Kadıköy");
        job.DropoffLatitude.ShouldBe(41.01);
        job.DropoffLongitude.ShouldBe(29.05);
        job.DropoffAddress.ShouldBe("Bağdat Cd. 7, Kadıköy");
        (await s.GetOutboxAsync("DispatchStarted.v1")).ShouldHaveSingleItem();
    }

    // ---- OrderStatusChanged: Cancelled ---------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_WhileAnOfferIsOpen_CancelsOfferAndJobAndFreesTheCourier()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        await s.OrderStatusChangedAsync(seeded.OrderId, "Cancelled", Cancel);

        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Cancelled);
        (await s.GetJobAsync(seeded.OrderId)).Status.ShouldBe(DispatchJobStatus.Cancelled);
        var courier = await s.GetCourierAsync(seeded.CourierId);
        courier.State.ShouldBe(CourierState.Available);
        courier.CurrentOrderId.ShouldBeNull();
        DispatchScenario.DataOf((await s.GetOutboxAsync("CourierStateChanged.v1")).ShouldHaveSingleItem())
            .GetProperty("state").GetString().ShouldBe("Available");
    }

    [Fact]
    public async Task Cancelled_WhileStillSearching_CancelsTheJob()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        await s.OrderStatusChangedAsync(orderId, "Cancelled", Cancel);

        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelled_AfterTheCourierWasAssigned_FreesTheCourierAndKeepsTheJobCompleted()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (orderId, courierId) = await s.SeedOnDeliveryAsync();

        await s.OrderStatusChangedAsync(orderId, "Cancelled", Cancel);

        (await s.GetCourierAsync(courierId)).State.ShouldBe(CourierState.Available);
        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Completed, "history: it was assigned before it was cancelled");
    }

    [Fact]
    public async Task Cancelled_RedeliveredAfterTheCourierTookNewWork_LeavesTheNewWorkAlone()
    {
        // At-least-once delivery: the same event can arrive again later, when the courier already has another order.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (oldOrder, courierId) = await s.SeedOnDeliveryAsync();
        await s.OrderStatusChangedAsync(oldOrder, "Cancelled", Cancel);

        var newOrder = await s.SeedJobAsync();
        await s.ExecuteAsync($"UPDATE couriers SET state = 'OnDelivery', current_order_id = {newOrder} WHERE id = {courierId}");
        await s.OrderStatusChangedAsync(oldOrder, "Cancelled", Cancel);

        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.OnDelivery, "the late duplicate is about the old order, not the new one");
        courier.CurrentOrderId.ShouldBe(newOrder);
    }

    [Fact]
    public async Task Cancelled_ForAnOrderDispatchHasNotSeenYet_FailsSoTheMessageIsRetried()
    {
        // OrderCreated and OrderStatusChanged travel on different queues; if the second overtakes the first, retry later
        // instead of acknowledging and forgetting (the job would then start for an order that is already cancelled).
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        await Should.ThrowAsync<InvalidOperationException>(() => s.OrderStatusChangedAsync(Guid.CreateVersion7(), "Cancelled", Cancel));
    }

    // ---- OrderStatusChanged: Delivered / Returned ----------------------------------------------------------------------

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Returned")]
    public async Task DeliveredOrReturned_FreesTheCourier(string status)
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (orderId, courierId) = await s.SeedOnDeliveryAsync();

        await s.OrderStatusChangedAsync(orderId, status, Cancel);

        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.Available);
        courier.CurrentOrderId.ShouldBeNull();
        DispatchScenario.DataOf((await s.GetOutboxAsync("CourierStateChanged.v1")).ShouldHaveSingleItem())
            .GetProperty("courierId").GetGuid().ShouldBe(courierId);
    }

    [Theory]
    [InlineData("Dispatching")]
    [InlineData("Assigned")]
    [InlineData("PickedUp")]
    [InlineData("Unassigned")]
    public async Task OtherStatuses_AreNotDispatchesBusiness(string status)
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (orderId, courierId) = await s.SeedOnDeliveryAsync();

        await s.OrderStatusChangedAsync(orderId, status, Cancel);

        (await s.GetCourierAsync(courierId)).State.ShouldBe(CourierState.OnDelivery);
        (await s.GetOutboxAsync()).ShouldBeEmpty();
    }
}
