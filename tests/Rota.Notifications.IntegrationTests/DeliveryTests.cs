using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Infrastructure;
using Rota.Sim.Merchant;

namespace Rota.Notifications.IntegrationTests;

/// <summary>Events in, webhooks out: the consumer's rows, the signed request, retries and the road to <c>Dead</c>. Integration tests against a real PostgreSQL and a real HTTP receiver.</summary>
public sealed class DeliveryTests(NotificationsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- The consumer ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Consumer_ForASubscribedMerchant_CreatesOnePendingDeliveryWithTheDocumentedBody()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var courierId = Guid.CreateVersion7();
        await s.CourierStateChangedAsync(courierId, "Mehmet K.", version: 3);
        var orderId = Guid.CreateVersion7();

        var eventId = await s.OrderStatusChangedAsync("m-1", "Delivered", orderVersion: 7, orderId, courierId);

        var delivery = (await s.DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.EventId.ShouldBe(eventId);
        delivery.EventType.ShouldBe("order.delivered");
        delivery.NextAttemptAt.ShouldBe(s.Time.GetUtcNow());
        using var body = System.Text.Json.JsonDocument.Parse(delivery.Payload);
        var root = body.RootElement;
        root.GetProperty("id").GetGuid().ShouldBe(eventId, "the body's id is the event id: that is what the merchant drops repeats by");
        root.GetProperty("type").GetString().ShouldBe("order.delivered");
        var data = root.GetProperty("data");
        data.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        data.GetProperty("status").GetString().ShouldBe("Delivered");
        data.GetProperty("orderVersion").GetInt32().ShouldBe(7);
        data.GetProperty("courier").GetProperty("id").GetGuid().ShouldBe(courierId);
        data.GetProperty("courier").GetProperty("displayName").GetString().ShouldBe("Mehmet K.");
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Dispatching")]
    public async Task Consumer_ForAStatusMerchantsAreNotToldAbout_CreatesNothing(string status)
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");

        await s.OrderStatusChangedAsync("m-1", status);

        (await s.DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Consumer_ForAMerchantWithoutASubscription_CreatesNothing()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");

        await s.OrderStatusChangedAsync("m-2", "Delivered");

        (await s.DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Consumer_TheSameEventTwice_IsRefusedByTheUniqueIndex()
    {
        // The inbox normally catches a redelivery first (covered in the messaging tests); this is the database's own second guard.
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var eventId = await s.OrderStatusChangedAsync("m-1", "Delivered");

        await Should.ThrowAsync<DbUpdateException>(() => s.OrderStatusChangedAsync("m-1", "Delivered", eventId: eventId));

        (await s.DeliveriesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task CourierName_AnOlderEventNeverOverwritesANewerName()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = Guid.CreateVersion7();

        await s.CourierStateChangedAsync(courierId, "New Name", version: 5);
        await s.CourierStateChangedAsync(courierId, "Old Name", version: 2);   // arrived late: no ordering between queues

        (await s.QueryAsync(db => db.CourierNames.AsNoTracking().SingleAsync(c => c.CourierId == courierId))).DisplayName.ShouldBe("New Name");
    }

    [Fact]
    public async Task Payload_ForACourierWeHaveNotHeardOf_HasNoName_ButStillIsSent()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");

        await s.OrderStatusChangedAsync("m-1", "Assigned", courierId: Guid.CreateVersion7());

        var delivery = (await s.DeliveriesAsync()).ShouldHaveSingleItem();
        System.Text.Json.JsonDocument.Parse(delivery.Payload).RootElement.GetProperty("data").GetProperty("courier")
            .GetProperty("displayName").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    // ---- The worker -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Worker_SendsASignedRequest_TheMerchantCanVerify()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var eventId = await s.OrderStatusChangedAsync("m-1", "PickedUp");

        (await s.RunRoundAsync(Cancel)).ShouldBe(1);

        var received = s.Merchant.ReceivedOn("a").ShouldHaveSingleItem();
        received.SignatureValid.ShouldBeTrue("verified by the merchant simulator's own code, from the stored body and the secret shown at subscription");
        received.EventId.ShouldBe(eventId);
        received.EventType.ShouldBe("order.picked_up");
        var delivery = (await s.DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        delivery.AttemptCount.ShouldBe(1);
        delivery.LastStatusCode.ShouldBe(200);
        delivery.DeliveredAt.ShouldBe(s.Time.GetUtcNow());
    }

    [Fact]
    public async Task Worker_ATamperedBodyFailsTheMerchantsCheck()
    {
        // What the merchant simulator does with a body that was changed on the way: the signature no longer matches.
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        var secret = await s.SubscribeAsync("m-1", "a");
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        await s.RunRoundAsync(Cancel);
        var received = s.Merchant.ReceivedOn("a").ShouldHaveSingleItem();

        WebhookVerifier.Verify(secret, received.Signature, received.Body, s.Time.GetUtcNow()).ShouldBe(VerifyOutcome.Valid);
        WebhookVerifier.Verify(secret, received.Signature, received.Body.Replace("Delivered", "Cancelled", StringComparison.Ordinal), s.Time.GetUtcNow())
            .ShouldBe(VerifyOutcome.BadSignature);
        WebhookVerifier.Verify(secret, received.Signature, received.Body, s.Time.GetUtcNow().AddMinutes(6)).ShouldBe(VerifyOutcome.TooOld);
    }

    [Fact]
    public async Task Worker_ANotYetDueDelivery_IsLeftAlone()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").StatusCode = 500;
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        await s.RunRoundAsync(Cancel);                   // fails, next attempt in 1 s

        (await s.RunRoundAsync(Cancel)).ShouldBe(0, "nothing is due yet");

        s.Merchant.ReceivedOn("a").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Worker_AFailedDelivery_IsRetriedAtTheBackoffTimes_AndSucceedsWhenTheMerchantRecovers()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").StatusCode = 503;
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        var start = s.Time.GetUtcNow();

        await s.RunRoundAsync(Cancel);
        var afterFirst = (await s.DeliveriesAsync()).Single();
        afterFirst.Status.ShouldBe(DeliveryStatus.Pending);
        afterFirst.AttemptCount.ShouldBe(1);
        afterFirst.LastStatusCode.ShouldBe(503);
        afterFirst.NextAttemptAt.ShouldBe(start + TimeSpan.FromSeconds(1));

        s.Fake.Advance(TimeSpan.FromSeconds(1));
        await s.RunRoundAsync(Cancel);
        (await s.DeliveriesAsync()).Single().NextAttemptAt.ShouldBe(start + TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(5), "second delay: 5 s");

        s.Merchant.Behavior("a").StatusCode = 200;
        s.Fake.Advance(TimeSpan.FromSeconds(5));
        await s.RunRoundAsync(Cancel);

        var done = (await s.DeliveriesAsync()).Single();
        done.Status.ShouldBe(DeliveryStatus.Succeeded);
        done.AttemptCount.ShouldBe(3);
    }

    [Fact]
    public async Task Worker_AfterSevenFailures_TheDeliveryIsDead_AndNoEighthRequestIsMade()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").StatusCode = 500;
        await s.OrderStatusChangedAsync("m-1", "Delivered");

        await s.RunRoundAsync(Cancel);
        for (var i = 0; i < 12; i++)
        {
            await s.AdvanceAndRunAsync(TimeSpan.FromMinutes(31), 1, Cancel);
        }

        var dead = (await s.DeliveriesAsync()).Single();
        dead.Status.ShouldBe(DeliveryStatus.Dead);
        dead.AttemptCount.ShouldBe(7);
        dead.NextAttemptAt.ShouldBeNull();
        dead.LastError.ShouldNotBeNullOrWhiteSpace();
        s.Merchant.ReceivedOn("a").Count.ShouldBe(7, "1 first attempt + 6 retries, then it stops");
    }

    [Fact]
    public async Task Worker_ASlowReceiver_CountsAsATimeout_AndTheRepeatCanBeDroppedByTheMerchantUsingTheId()
    {
        // At-least-once in practice: the merchant processed the event but answered too late, so Rota could not know and sends it again.
        var quick = new Dictionary<string, string> { ["Notifications:DeliveryTimeout"] = "00:00:00.500", ["Notifications:Lease"] = "00:00:30" };
        await using var s = await NotificationsScenario.StartAsync(fixture, quick, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").Delay = TimeSpan.FromSeconds(2);
        await s.OrderStatusChangedAsync("m-1", "Delivered");

        await s.RunRoundAsync(Cancel);
        var failed = (await s.DeliveriesAsync()).Single();
        failed.Status.ShouldBe(DeliveryStatus.Pending);
        failed.LastError!.ShouldContain("Timeout");

        s.Merchant.Behavior("a").Delay = TimeSpan.Zero;
        s.Fake.Advance(TimeSpan.FromSeconds(1));
        await s.RunRoundAsync(Cancel);

        (await s.DeliveriesAsync()).Single().Status.ShouldBe(DeliveryStatus.Succeeded);
        var received = s.Merchant.ReceivedOn("a");
        received.Count.ShouldBe(2, "the same event arrived twice");
        received.Select(r => r.EventId).Distinct().Count().ShouldBe(1, "but it is one event: the id tells the repeat apart");
    }

    [Fact]
    public async Task Worker_ARedirectIsAFailure_AndIsNotFollowed()
    {
        // A redirect could lead anywhere, including an internal address the URL check never saw.
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").RedirectTo = s.Merchant.UrlFor("elsewhere");
        await s.OrderStatusChangedAsync("m-1", "Delivered");

        await s.RunRoundAsync(Cancel);

        (await s.DeliveriesAsync()).Single().Status.ShouldBe(DeliveryStatus.Pending);
        s.Merchant.ReceivedOn("a").Count.ShouldBe(1);
        s.Merchant.ReceivedOn("elsewhere").ShouldBeEmpty("the redirect target must never be contacted");
    }

    [Fact]
    public async Task Worker_ADeliveryForAMerchantWhoLostTheSubscription_FailsVisibly()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        await s.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM webhook_subscriptions WHERE merchant_id = 'm-1'"));

        await s.RunRoundAsync(Cancel);

        var delivery = (await s.DeliveriesAsync()).Single();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.LastError!.ShouldContain("no active webhook subscription");
        s.Merchant.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Worker_AtSendTime_RefusesAHostThatResolvesToAPrivateAddress()
    {
        // The URL was accepted earlier (here: written straight into the table). The connection still checks the address it really connects to.
        var strict = new Dictionary<string, string> { ["Notifications:AllowPrivateNetworks"] = "false" };
        await using var s = await NotificationsScenario.StartAsync(fixture, strict, cancellationToken: Cancel);
        await s.QueryAsync(async db =>
        {
            var protectedSecret = s.Api.Services.GetRequiredService<SecretProtector>().Protect("whsec_x");
            db.Subscriptions.Add(WebhookSubscription.Create("m-1", s.Merchant.UrlFor("a"), protectedSecret, s.Time.GetUtcNow()));
            await db.SaveChangesAsync();
            return 0;
        });
        await s.OrderStatusChangedAsync("m-1", "Delivered");

        await s.RunRoundAsync(Cancel);

        var delivery = (await s.DeliveriesAsync()).Single();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.LastError!.ShouldContain("private or internal");
        s.Merchant.Received.ShouldBeEmpty("no request may leave for a private address");
    }
}
