using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Features;
using Rota.Orders.IntegrationTests.Support;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.IntegrationTests.Features;

/// <summary>
/// The courier's side of an order: pickup, deliver (with the customer's code), delivery-failed, returned, and the dispatcher's unlock.
/// API tests against a real PostgreSQL: status codes, ownership (404, not 403), roles, the five-attempt lock, and the event that
/// leaves with every change.
/// </summary>
public sealed class DeliveryTests(OrdersDatabaseFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Pickup ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pickup_ByTheAssignedCourier_Returns200_RecordsHistoryAndEvent()
    {
        var built = await SeedAsync(OrderStatus.Assigned);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<OrderResponse>(Cancel))!.Status.ShouldBe("PickedUp");

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == built.Order.Id, Cancel);
        saved.History.Single(h => h.To == OrderStatus.PickedUp).Actor.ShouldBe(OrderActor.Courier(built.CourierId));
        var change = (await OutboxAsync(db, built.Order.Id)).ShouldHaveSingleItem();
        change.Type.ShouldBe("OrderStatusChanged.v1");
        change.Payload.ShouldContain("PickedUp");
        change.Payload.ShouldContain(built.CourierId.ToString("D"));
    }

    [Fact]
    public async Task Pickup_ByACourierWhoIsNotAssigned_Returns404AndChangesNothing()
    {
        // F06 PASS 3. "Not yours" looks exactly like "does not exist" (docs/06 §2).
        var built = await SeedAsync(OrderStatus.Assigned);
        using var stranger = fixture.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()));

        using var response = await stranger.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", Cancel);
        (await StatusAsync(built)).ShouldBe(OrderStatus.Assigned);
    }

    [Fact]
    public async Task Pickup_OfAnOrderThatHasNoCourierYet_Returns404()
    {
        var built = await SeedAsync(OrderStatus.Dispatching);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", Cancel);
    }

    [Theory]
    [InlineData("merchant")]
    [InlineData("dispatcher")]
    public async Task Pickup_ByAnotherRole_Returns403(string role)
    {
        var built = await SeedAsync(OrderStatus.Assigned);
        using var client = fixture.Api.CreateClientAs(role == "merchant" ? DevUsers.Merchant(DevUsers.NewMerchantId()) : DevUsers.Dispatcher("dsp-1"));

        using var response = await client.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", Cancel);
    }

    [Fact]
    public async Task Pickup_WithoutIdentity_Returns401()
    {
        var built = await SeedAsync(OrderStatus.Assigned);
        using var anonymous = fixture.Api.CreateClient();

        using var response = await anonymous.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pickup_Twice_SecondCallIs409AndWritesNoSecondEvent()
    {
        var built = await SeedAsync(OrderStatus.Assigned);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));
        (await courier.PostAsync(Uri(built, "pickup"), content: null, Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await courier.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        var problem = await second.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
        problem.GetProperty("currentStatus").GetString().ShouldBe("PickedUp");
        await using var db = fixture.CreateDbContext();
        (await OutboxAsync(db, built.Order.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pickup_AfterTheMerchantCancelled_Returns409WithCurrentStatus()
    {
        // The order still remembers its courier, so this is "wrong state" (409), not "not yours" (404).
        var merchantId = DevUsers.NewMerchantId();
        var built = await SeedAsync(OrderStatus.Assigned, merchantId);
        using var merchant = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));
        (await merchant.PostAsync(Uri(built, "cancel"), content: null, Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "pickup"), content: null, Cancel);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
        problem.GetProperty("currentStatus").GetString().ShouldBe("Cancelled");
    }

    // ---- Deliver and the five-attempt lock ---------------------------------------------------------------------------

    [Fact]
    public async Task Deliver_WithTheCorrectCode_Returns200_AndReportsDelivered()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = built.DeliveryCode }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<OrderResponse>(Cancel))!.Status.ShouldBe("Delivered");
        await using var db = fixture.CreateDbContext();
        (await OutboxAsync(db, built.Order.Id)).ShouldHaveSingleItem().Payload.ShouldContain("Delivered");
    }

    [Fact]
    public async Task Deliver_WithAWrongCode_Returns400WithRemainingAttempts_AndCountsTheAttempt()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-delivery-code", Cancel);
        problem.GetProperty("remainingAttempts").GetInt32().ShouldBe(4);
        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.SingleAsync(o => o.Id == built.Order.Id, Cancel);
        saved.DeliveryCode.FailedAttempts.ShouldBe(1, "the failed attempt must be saved, or the limit never triggers");
        saved.Status.ShouldBe(OrderStatus.PickedUp);
        (await OutboxAsync(db, built.Order.Id)).ShouldBeEmpty("a wrong code is not a status change");
    }

    [Fact]
    public async Task Deliver_FiveWrongCodes_LockTheCode_ThenEvenTheRightCodeIs409_UntilADispatcherUnlocks()
    {
        // F06 PASS 2, as one story.
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var wrong = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);
            var problem = await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-delivery-code", Cancel);
            problem.GetProperty("remainingAttempts").GetInt32().ShouldBe(5 - attempt);
        }

        using var locked = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = built.DeliveryCode }, Cancel);
        await locked.ShouldBeProblemAsync(HttpStatusCode.Conflict, "delivery-code-locked", Cancel);
        (await GetAsync(built, DevUsers.Dispatcher("dsp-1"))).DeliveryCodeLocked.ShouldBeTrue();

        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1"));
        using var unlock = await dispatcher.PostAsync(Uri(built, "delivery-code/unlock"), content: null, Cancel);
        unlock.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await unlock.Content.ReadFromJsonAsync<OrderResponse>(Cancel))!.DeliveryCodeLocked.ShouldBeFalse();

        using var delivered = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = built.DeliveryCode }, Cancel);
        delivered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(built)).ShouldBe(OrderStatus.Delivered);
    }

    [Fact]
    public async Task Deliver_AfterUnlock_GivesAllFiveAttemptsBack()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));
        for (var i = 0; i < 5; i++)
        {
            await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);
        }

        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1"));
        await dispatcher.PostAsync(Uri(built, "delivery-code/unlock"), content: null, Cancel);
        using var again = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);

        var problem = await again.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-delivery-code", Cancel);
        problem.GetProperty("remainingAttempts").GetInt32().ShouldBe(4);
    }

    [Theory]
    [InlineData("48a1")]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("")]
    public async Task Deliver_WithAMalformedCode_Returns400_WithoutConsumingAnAttempt(string code)
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", Cancel);
        await using var db = fixture.CreateDbContext();
        (await db.Orders.SingleAsync(o => o.Id == built.Order.Id, Cancel)).DeliveryCode.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Deliver_WithoutABody_Returns400()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "deliver"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", Cancel);
    }

    [Fact]
    public async Task Deliver_BeforePickup_Returns409_AndABadCodeDoesNotBurnAnAttempt()
    {
        // A code means nothing before the parcel is out for delivery; guessing at an Assigned order must not lock it.
        var built = await SeedAsync(OrderStatus.Assigned);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
        await using var db = fixture.CreateDbContext();
        (await db.Orders.SingleAsync(o => o.Id == built.Order.Id, Cancel)).DeliveryCode.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Deliver_ByACourierWhoIsNotAssigned_Returns404AndBurnsNoAttempt()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var stranger = fixture.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()));

        using var response = await stranger.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", Cancel);
        await using var db = fixture.CreateDbContext();
        (await db.Orders.SingleAsync(o => o.Id == built.Order.Id, Cancel)).DeliveryCode.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Deliver_Twice_SecondCallIs409()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));
        (await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = built.DeliveryCode }, Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = built.DeliveryCode }, Cancel);

        await second.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
    }

    [Fact]
    public async Task Deliver_TenWrongGuessesAtOnce_NeverGiveMoreThanFiveTries_AndEveryCountedGuessWasAnswered()
    {
        // Brute force in parallel: all ten read "0 failed attempts"; the xmin condition lets only the writes that
        // saw the latest row through, so the stored counter equals the number of "wrong code" answers and never exceeds 5.
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guesses = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.Task;
            using var response = await courier.PostAsJsonAsync(Uri(built, "deliver"), new { code = WrongCode(built) }, Cancel);
            return response.StatusCode;
        }).ToArray();
        start.SetResult();

        var statuses = await Task.WhenAll(guesses);

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.SingleAsync(o => o.Id == built.Order.Id, Cancel);
        var answeredWrong = statuses.Count(s => s == HttpStatusCode.BadRequest);
        saved.DeliveryCode.FailedAttempts.ShouldBe(answeredWrong);
        saved.DeliveryCode.FailedAttempts.ShouldBeLessThanOrEqualTo(DeliveryCode.MaxFailedAttempts);
        statuses.ShouldAllBe(s => s == HttpStatusCode.BadRequest || s == HttpStatusCode.Conflict);
        saved.Status.ShouldBe(OrderStatus.PickedUp);
    }

    // ---- Delivery failed and returned --------------------------------------------------------------------------------

    [Fact]
    public async Task DeliveryFailed_ThenReturned_WalksTheOrderToItsEnd()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var failed = await courier.PostAsJsonAsync(Uri(built, "delivery-failed"), new { reason = "customer absent" }, Cancel);
        failed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var returned = await courier.PostAsync(Uri(built, "returned"), content: null, Cancel);
        returned.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == built.Order.Id, Cancel);
        saved.Status.ShouldBe(OrderStatus.Returned);
        saved.History.Single(h => h.To == OrderStatus.DeliveryFailed).Reason.ShouldBe("customer absent");
        var events = await OutboxAsync(db, built.Order.Id);
        events.Count.ShouldBe(2);
        events.ShouldContain(e => e.Payload.Contains("customer absent"));
    }

    [Fact]
    public async Task DeliveryFailed_WithoutABody_IsAccepted()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "delivery-failed"), content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeliveryFailed_WithATooLongReason_Returns400()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsJsonAsync(Uri(built, "delivery-failed"), new { reason = new string('x', 301) }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(built)).ShouldBe(OrderStatus.PickedUp);
    }

    [Fact]
    public async Task Returned_ByADispatcher_Works()
    {
        var built = await SeedAsync(OrderStatus.DeliveryFailed);
        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-3"));

        using var response = await dispatcher.PostAsync(Uri(built, "returned"), content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.CreateDbContext();
        var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == built.Order.Id, Cancel);
        saved.History.Single(h => h.To == OrderStatus.Returned).Actor.ShouldBe(OrderActor.Dispatcher("dsp-3"));
    }

    [Fact]
    public async Task Returned_ByAMerchant_Returns403()
    {
        var built = await SeedAsync(OrderStatus.DeliveryFailed);
        using var merchant = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await merchant.PostAsync(Uri(built, "returned"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", Cancel);
    }

    [Fact]
    public async Task Returned_BeforeAFailedDelivery_Returns409()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "returned"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
    }

    // ---- Unlock --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unlock_WhenNotLocked_IsAHarmlessSuccess()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1"));

        using var response = await dispatcher.PostAsync(Uri(built, "delivery-code/unlock"), content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unlock_WhenTheParcelIsNotOutForDelivery_Returns409()
    {
        var built = await SeedAsync(OrderStatus.Assigned);
        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1"));

        using var response = await dispatcher.PostAsync(Uri(built, "delivery-code/unlock"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-order-transition", Cancel);
    }

    [Fact]
    public async Task Unlock_ByTheCourier_Returns403_SoACourierCannotUnlockTheirOwnCode()
    {
        var built = await SeedAsync(OrderStatus.PickedUp);
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));

        using var response = await courier.PostAsync(Uri(built, "delivery-code/unlock"), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", Cancel);
    }

    [Fact]
    public async Task Unlock_OfAnUnknownOrder_Returns404()
    {
        using var dispatcher = fixture.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1"));

        using var response = await dispatcher.PostAsync(new Uri($"/api/v1/orders/{Guid.CreateVersion7()}/delivery-code/unlock", UriKind.Relative), content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found", Cancel);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>Any well-formed code that is not the order's own.</summary>
    private static string WrongCode(BuiltOrder built) => built.DeliveryCode == "0000" ? "1111" : "0000";

    private static Uri Uri(BuiltOrder built, string action) => new($"/api/v1/orders/{built.Order.Id}/{action}", UriKind.Relative);

    private async Task<BuiltOrder> SeedAsync(OrderStatus status, string? merchantId = null)
    {
        var built = new OrderBuilder().ForMerchant(merchantId ?? DevUsers.NewMerchantId()).InStatus(status).Build();
        await using var db = fixture.CreateDbContext();
        db.Orders.Add(built.Order);
        await db.SaveChangesAsync(Cancel);
        return built;
    }

    private async Task<OrderStatus> StatusAsync(BuiltOrder built)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Orders.Where(o => o.Id == built.Order.Id).Select(o => o.Status).SingleAsync(Cancel);
    }

    private async Task<OrderResponse> GetAsync(BuiltOrder built, string devUser)
    {
        using var client = fixture.Api.CreateClientAs(devUser);
        return (await client.GetFromJsonAsync<OrderResponse>(new Uri($"/api/v1/orders/{built.Order.Id}", UriKind.Relative), Cancel))!;
    }

    private static Task<List<OutboxMessage>> OutboxAsync(Api.Infrastructure.OrdersDbContext db, Guid orderId) =>
        db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'aggregateId' = {orderId.ToString()}")
            .ToListAsync(Cancel);
}
