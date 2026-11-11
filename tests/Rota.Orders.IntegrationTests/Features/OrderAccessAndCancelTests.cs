using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Features;
using Rota.Orders.IntegrationTests.Support;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.IntegrationTests.Features;

/// <summary><c>GET /orders/{id}</c> and <c>POST /orders/{id}/cancel</c>: ownership (R-10), transitions, concurrency.</summary>
public sealed class OrderAccessAndCancelTests(OrdersDatabaseFixture fixture)
{
    // ---- Ownership (R-10): "not yours" looks exactly like "does not exist" ------------------

    [Fact]
    public async Task Get_OrderOfAnotherMerchant_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = await SeedAsync(new OrderBuilder().ForMerchant(DevUsers.NewMerchantId()), ct);
        using var stranger = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await stranger.GetAsync(OrderUri(order.Id), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", ct);
    }

    [Fact]
    public async Task Get_UnknownOrder_Returns404LikeAForeignOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await client.GetAsync(OrderUri(Guid.CreateVersion7()), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", ct);
    }

    [Fact]
    public async Task Get_AsOwnerDispatcherOrAssignedCourier_Returns200WithoutDeliveryCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        var courierId = Guid.CreateVersion7();
        var order = await SeedAsync(new OrderBuilder().ForMerchant(merchantId).WithCourier(courierId).InStatus(OrderStatus.Assigned), ct);

        foreach (var user in new[] { DevUsers.Merchant(merchantId), DevUsers.Dispatcher("dispatcher-1"), DevUsers.Courier(courierId) })
        {
            using var client = fixture.Api.CreateClientAs(user);
            using var response = await client.GetAsync(OrderUri(order.Id), ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, user);
            var text = await response.Content.ReadAsStringAsync(ct);
            text.ShouldNotContain("deliveryCode\"", Case.Insensitive, user); // neither the code nor its hash
            var body = (await response.Content.ReadFromJsonAsync<OrderResponse>(ct)).ShouldNotBeNull();
            body.Status.ShouldBe("Assigned");
            body.AssignedCourierId.ShouldBe(courierId);
        }
    }

    [Fact]
    public async Task Get_AsCourierNotAssigned_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = await SeedAsync(new OrderBuilder().InStatus(OrderStatus.Assigned), ct);
        using var otherCourier = fixture.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()));

        using var response = await otherCourier.GetAsync(OrderUri(order.Id), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", ct);
    }

    [Fact]
    public async Task Cancel_OrderOfAnotherMerchant_Returns404AndLeavesItUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = await SeedAsync(new OrderBuilder(), ct);
        using var stranger = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await stranger.PostAsync(CancelUri(order.Id), content: null, ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", ct);
        await using var db = fixture.CreateDbContext();
        (await db.Orders.SingleAsync(o => o.Id == order.Id, ct)).Status.ShouldBe(OrderStatus.Created);
    }

    // ---- Cancel: happy path, invalid transitions, roles ------------------------------------

    [Fact]
    public async Task Cancel_CreatedOrderByOwner_Returns200_RecordsHistoryAndEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        var order = await SeedAsync(new OrderBuilder().ForMerchant(merchantId), ct);
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));

        using var response = await client.PostAsJsonAsync(CancelUri(order.Id), new { reason = "customer called" }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<OrderResponse>(ct)).ShouldNotBeNull();
        body.Status.ShouldBe("Cancelled");
        body.OrderVersion.ShouldBe(2);

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == order.Id, ct);
        var change = saved.History.Where(h => h.To == OrderStatus.Cancelled).ShouldHaveSingleItem();
        change.Reason.ShouldBe("customer called");
        change.Actor.ShouldBe(OrderActor.Merchant(merchantId));
        var events = await OutboxForAsync(db, order.Id, ct);
        events.ShouldHaveSingleItem().Type.ShouldBe("OrderStatusChanged.v1");
    }

    [Theory]
    [InlineData(OrderStatus.PickedUp)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task Cancel_WhenStatusDoesNotAllowIt_Returns409WithCurrentStatus(OrderStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        var order = await SeedAsync(new OrderBuilder().ForMerchant(merchantId).InStatus(status), ct);
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));

        using var response = await client.PostAsync(CancelUri(order.Id), content: null, ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", ct);
        problem.GetProperty("currentStatus").GetString().ShouldBe(status.ToString());
    }

    [Fact]
    public async Task Cancel_AsDispatcher_Returns200AndRecordsDispatcherActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = await SeedAsync(new OrderBuilder().InStatus(OrderStatus.Assigned), ct);
        using var client = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dispatcher-7"));

        using var response = await client.PostAsync(CancelUri(order.Id), content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == order.Id, ct);
        saved.History.Single(h => h.To == OrderStatus.Cancelled).Actor.ShouldBe(OrderActor.Dispatcher("dispatcher-7"));
    }

    [Fact]
    public async Task Cancel_AsCourier_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        var courierId = Guid.CreateVersion7();
        var order = await SeedAsync(new OrderBuilder().WithCourier(courierId).InStatus(OrderStatus.Assigned), ct);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(courierId));

        using var response = await courier.PostAsync(CancelUri(order.Id), content: null, ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", ct);
    }

    // ---- Concurrency: many cancels of one order --------------------------------------------

    [Fact]
    public async Task Cancel_TenConcurrentRequests_ExactlyOneSucceedsAndOneEventIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 10; round++)
        {
            var merchantId = DevUsers.NewMerchantId();
            var order = await SeedAsync(new OrderBuilder().ForMerchant(merchantId), ct);
            using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 10).Select(async _ =>
            {
                await start.Task;
                using var response = await client.PostAsync(CancelUri(order.Id), content: null, ct);
                return response.StatusCode;
            }).ToArray();
            start.SetResult();
            var statuses = await Task.WhenAll(requests);

            // Losers see either the new status (409 invalid transition) or a stale xmin (409 conflict).
            statuses.Count(s => s == HttpStatusCode.OK).ShouldBe(1, $"round {round}");
            statuses.Count(s => s == HttpStatusCode.Conflict).ShouldBe(9, $"round {round}");
            await using var db = fixture.CreateDbContext();
            var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == order.Id, ct);
            saved.History.Count(h => h.To == OrderStatus.Cancelled).ShouldBe(1, $"round {round}");
            saved.Version.ShouldBe(2, $"round {round}");
            (await OutboxForAsync(db, order.Id, ct)).Count.ShouldBe(1, $"round {round}");
        }
    }

    private async Task<Order> SeedAsync(OrderBuilder builder, CancellationToken ct)
    {
        var order = builder.Build().Order;
        await using var db = fixture.CreateDbContext();
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }

    private static Task<List<Rota.BuildingBlocks.Messaging.OutboxMessage>> OutboxForAsync(
        Api.Infrastructure.OrdersDbContext db, Guid orderId, CancellationToken ct) =>
        db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'aggregateId' = {orderId.ToString()}")
            .ToListAsync(ct);

    private static Uri OrderUri(Guid id) => new($"/api/v1/orders/{id}", UriKind.Relative);

    private static Uri CancelUri(Guid id) => new($"/api/v1/orders/{id}/cancel", UriKind.Relative);
}
