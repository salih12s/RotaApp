using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Rota.Notifications.Api.Domain;
using Rota.TestKit;

namespace Rota.Notifications.IntegrationTests;

/// <summary>
/// The promises about a merchant that is down, slow or failing: nothing is lost (R-9), one slow merchant does not slow the others (bulkhead),
/// a failing one is cut off for a while (circuit breaker), and the secrets survive a restart (Data Protection key ring on disk).
/// </summary>
public sealed class ResilienceTests(NotificationsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R9_ReceiverDownForFiveMinutes_EveryEventIsDeliveredOnceItIsBack_AndRepeatsAreToldApartById()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").StatusCode = 503;      // the merchant's server is down
        var eventIds = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            eventIds.Add(await s.OrderStatusChangedAsync("m-1", "Delivered", orderVersion: i + 2));
        }

        // Five minutes pass, in ten-second steps; the worker wakes up at every step and keeps being refused.
        await s.RunRoundAsync(Cancel);
        await s.AdvanceAndRunAsync(TimeSpan.FromSeconds(10), 30, Cancel);
        (await s.CountAsync(DeliveryStatus.Succeeded)).ShouldBe(0);
        (await s.CountAsync(DeliveryStatus.Dead)).ShouldBe(0, "five minutes of trouble must not use up the attempts: the schedule stretches to 30 minutes");

        // The merchant is back. The deliveries are due at their next backoff time; time keeps passing, the worker keeps waking up.
        s.Merchant.Behavior("a").StatusCode = 200;
        await s.AdvanceAndRunAsync(TimeSpan.FromSeconds(30), 60, Cancel);

        (await s.CountAsync(DeliveryStatus.Succeeded)).ShouldBe(20, "every event arrived");
        (await s.CountAsync(DeliveryStatus.Dead)).ShouldBe(0);
        var ok = s.Merchant.ReceivedOn("a").Where(r => r.SignatureValid).ToList();
        ok.Select(r => r.EventId).Distinct().Count().ShouldBe(20);
        eventIds.ShouldAllBe(id => ok.Any(r => r.EventId == id));
        s.Merchant.ReceivedOn("a").ShouldAllBe(r => r.SignatureValid, "every retry is signed afresh and verifies");
    }

    [Fact]
    public async Task Bulkhead_ASlowMerchantDoesNotSlowAFastOne()
    {
        // F07 PASS 3, in real time: merchant A takes 3 s per request, merchant B answers at once. B's deliveries must not queue behind A's.
        var settings = new Dictionary<string, string>
        {
            ["Notifications:WorkerEnabled"] = "true",
            ["Notifications:WorkerPollInterval"] = "00:00:00.100",
        };
        await using var s = await NotificationsScenario.StartAsync(fixture, settings, time: TimeProvider.System, cancellationToken: Cancel);
        await s.SubscribeAsync("m-a", "a");
        await s.SubscribeAsync("m-b", "b");
        s.Merchant.Behavior("a").Delay = TimeSpan.FromSeconds(3);

        // One warm-up delivery to B first: the very first request of a fresh process pays for JIT and connection setup, which says nothing
        // about starvation (and would make this timing test depend on how busy the machine is).
        await s.OrderStatusChangedAsync("m-b", "Delivered", orderVersion: 1);
        await Eventually.UntilAsync(
            async () => await s.QueryAsync(db => db.Deliveries.CountAsync(d => d.MerchantId == "m-b" && d.Status == DeliveryStatus.Succeeded)) == 1,
            TimeSpan.FromSeconds(20), "the warm-up delivery arrives", Cancel);

        var warmedUpAt = s.Time.GetUtcNow();
        for (var i = 0; i < 12; i++)
        {
            await s.OrderStatusChangedAsync("m-a", "Delivered", orderVersion: i + 2);
        }

        for (var i = 0; i < 40; i++)
        {
            await s.OrderStatusChangedAsync("m-b", "Delivered", orderVersion: i + 2);
        }

        await Eventually.UntilAsync(
            async () => await s.QueryAsync(db => db.Deliveries.CountAsync(d => d.MerchantId == "m-b" && d.Status == DeliveryStatus.Succeeded)) == 41,
            TimeSpan.FromSeconds(20), "all of merchant B's deliveries arrive", Cancel);

        var latencies = (await s.QueryAsync(db => db.Deliveries.AsNoTracking().Where(d => d.MerchantId == "m-b" && d.CreatedAt > warmedUpAt).ToListAsync()))
            .Select(d => (d.DeliveredAt!.Value - d.CreatedAt).TotalSeconds).Order().ToList();
        var p95 = latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1];
        p95.ShouldBeLessThan(2.0, $"B's p95 was {p95:0.00} s while A was taking 3 s per request");

        // A was never dropped, only slow: it is still being worked through, four at a time.
        (await s.QueryAsync(db => db.Deliveries.CountAsync(d => d.MerchantId == "m-a" && d.Status == DeliveryStatus.Dead))).ShouldBe(0);
    }

    [Fact]
    public async Task Bulkhead_NeverMoreThanFourRequestsInFlightToOneMerchant()
    {
        var settings = new Dictionary<string, string> { ["Notifications:MerchantConcurrency"] = "4" };
        await using var s = await NotificationsScenario.StartAsync(fixture, settings, cancellationToken: Cancel);
        await s.SubscribeAsync("m-a", "a");
        s.Merchant.Behavior("a").Delay = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 8; i++)
        {
            await s.OrderStatusChangedAsync("m-a", "Delivered", orderVersion: i + 2);
        }

        var clock = Stopwatch.StartNew();
        await s.RunRoundAsync(Cancel);   // eight are claimed; only four get a slot, four are handed back untouched
        clock.Stop();

        s.Merchant.ReceivedOn("a").Count.ShouldBe(4);
        var all = await s.DeliveriesAsync();
        all.Count(d => d.Status == DeliveryStatus.Succeeded).ShouldBe(4);
        all.Where(d => d.Status == DeliveryStatus.Pending).ShouldAllBe(d => d.AttemptCount == 0, "a hand-back is not a failed attempt");
    }

    [Fact]
    public async Task Circuit_AMerchantThatKeepsFailing_IsLeftAloneForAMinute_ThenTriedAgain()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        s.Merchant.Behavior("a").StatusCode = 500;
        for (var i = 0; i < 12; i++)
        {
            await s.OrderStatusChangedAsync("m-1", "Delivered", orderVersion: i + 2);
        }

        // Rounds until five failures in a row have been seen (the bulkhead lets four through per round).
        for (var round = 0; round < 6 && s.Merchant.ReceivedOn("a").Count < 5; round++)
        {
            await s.RunRoundAsync(Cancel);
            s.Fake.Advance(TimeSpan.FromSeconds(1));
        }

        var before = s.Merchant.ReceivedOn("a").Count;
        before.ShouldBeGreaterThanOrEqualTo(5);

        await s.RunRoundAsync(Cancel);
        s.Merchant.ReceivedOn("a").Count.ShouldBe(before, "the circuit is open: no request leaves for this merchant");

        s.Fake.Advance(TimeSpan.FromMinutes(1));
        await s.RunRoundAsync(Cancel);
        s.Merchant.ReceivedOn("a").Count.ShouldBeGreaterThan(before, "after the pause the merchant is tried again");
    }

    [Fact]
    public async Task Circuit_OneMerchantBeingCutOff_DoesNotStopAnother()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-a", "a");
        await s.SubscribeAsync("m-b", "b");
        s.Merchant.Behavior("a").StatusCode = 500;
        for (var i = 0; i < 8; i++)
        {
            await s.OrderStatusChangedAsync("m-a", "Delivered", orderVersion: i + 2);
        }

        for (var round = 0; round < 4; round++)
        {
            await s.RunRoundAsync(Cancel);
            s.Fake.Advance(TimeSpan.FromSeconds(1));
        }

        await s.OrderStatusChangedAsync("m-b", "Delivered");
        await s.RunRoundAsync(Cancel);

        s.Merchant.ReceivedOn("b").ShouldHaveSingleItem().SignatureValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Restart_ExistingSecretsStillSign_BecauseTheKeyRingLivesOnDisk()
    {
        // F07 PASS 6. First life of the service: subscribe, so a secret is created, encrypted and stored.
        var connection = await fixture.CreateDatabaseAsync(Cancel);
        var keys = Path.Combine(Path.GetTempPath(), $"rota-keys-{Guid.NewGuid():N}");
        string secret;
        await using (var first = await NotificationsScenario.StartAsync(fixture, connection: connection, keyDirectory: keys, cancellationToken: Cancel))
        {
            secret = await first.SubscribeAsync("m-1", "a");
        }

        // Second life: a new process in real life (new host here), same database, same key directory.
        await using var second = await NotificationsScenario.StartAsync(fixture, connection: connection, keyDirectory: keys, cancellationToken: Cancel);
        second.Merchant.SetSecret("a", secret);
        using (var client = second.ClientAs(DevUsers.Merchant("m-1")))
        {
            // The merchant points the subscription at the new fake server (its port changed); the stored secret stays.
            (await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = second.Merchant.UrlFor("a") }, Cancel)).EnsureSuccessStatusCode();
        }

        await second.OrderStatusChangedAsync("m-1", "Delivered");
        await second.RunRoundAsync(Cancel);

        second.Merchant.ReceivedOn("a").ShouldHaveSingleItem().SignatureValid.ShouldBeTrue("signed with the secret created before the restart");
    }

    [Fact]
    public async Task Restart_WithoutTheKeyRing_TheSecretCannotBeRead_AndTheDeliveryFailsVisiblyInsteadOfBeingSignedWrong()
    {
        // The reason the key ring must persist: a new, empty key directory makes every stored secret unreadable.
        var connection = await fixture.CreateDatabaseAsync(Cancel);
        await using (var first = await NotificationsScenario.StartAsync(fixture, connection: connection, cancellationToken: Cancel))
        {
            await first.SubscribeAsync("m-1", "a");
        }

        await using var second = await NotificationsScenario.StartAsync(fixture, connection: connection, cancellationToken: Cancel);
        await second.OrderStatusChangedAsync("m-1", "Delivered");
        await second.RunRoundAsync(Cancel);

        var delivery = (await second.DeliveriesAsync()).Single();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.LastError!.ShouldContain("cannot be decrypted");
        second.Merchant.Received.ShouldBeEmpty();
    }
}
