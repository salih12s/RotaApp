using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rota.Notifications.Api.Domain;
using Rota.TestKit;

namespace Rota.Notifications.IntegrationTests;

/// <summary>The merchant's side over HTTP: subscribe, rotate the secret, look at and replay deliveries. API tests: status codes, ownership, validation.</summary>
public sealed class SubscriptionAndHistoryTests(NotificationsFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);
        response.StatusCode.ShouldBe(status, text);
        var body = JsonDocument.Parse(text).RootElement.Clone();
        body.GetProperty("type").GetString().ShouldBe($"https://rota.dev/errors/{code}", text);
        return body;
    }

    // ---- Subscription ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Put_FirstTime_CreatesTheSubscriptionAndShowsTheSecretOnce()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var first = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = s.Merchant.UrlFor("a") }, Cancel);
        using var second = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = s.Merchant.UrlFor("b") }, Cancel);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await first.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        created.GetProperty("secret").GetString().ShouldStartWith("whsec_");
        created.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        var updated = await second.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        updated.GetProperty("secret").ValueKind.ShouldBe(JsonValueKind.Null, "the secret is shown once, when it is created");
        updated.GetProperty("url").GetString().ShouldEndWith("/b");
    }

    [Fact]
    public async Task Put_StoresTheSecretEncrypted_NotAsPlainText()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);

        var secret = await s.SubscribeAsync("m-1", "a");

        var stored = await s.QueryAsync(db => db.Subscriptions.AsNoTracking().SingleAsync(x => x.MerchantId == "m-1"));
        stored.SecretProtected.ShouldNotBe(secret);
        stored.SecretProtected.ShouldNotContain(secret);
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::1]/hook")]
    [InlineData("http://merchant.example.com/hook")]      // plain http outside development
    [InlineData("https://user:pw@merchant.example.com/hook")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Put_WithAnUnsafeOrInvalidUrl_Returns400_AndSavesNothing(string url)
    {
        // F07 PASS 4. The strict, production-like configuration: no http, no private networks.
        var strict = new Dictionary<string, string> { ["Notifications:AllowHttp"] = "false", ["Notifications:AllowPrivateNetworks"] = "false" };
        await using var s = await NotificationsScenario.StartAsync(fixture, strict, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url }, Cancel);

        await ProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        (await s.QueryAsync(db => db.Subscriptions.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Put_WithAHostNameThatResolvesToLoopback_Returns400()
    {
        // The name looks harmless; where it points is what matters. "localhost" resolves to 127.0.0.1 / ::1.
        var strict = new Dictionary<string, string> { ["Notifications:AllowHttp"] = "false", ["Notifications:AllowPrivateNetworks"] = "false" };
        await using var s = await NotificationsScenario.StartAsync(fixture, strict, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = "https://localhost/hook" }, Cancel);

        await ProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Put_WithAHostNameThatDoesNotResolve_Returns400()
    {
        var strict = new Dictionary<string, string> { ["Notifications:AllowHttp"] = "false", ["Notifications:AllowPrivateNetworks"] = "false" };
        await using var s = await NotificationsScenario.StartAsync(fixture, strict, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = "https://no-such-host.invalid/hook" }, Cancel);

        await ProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Put_AsCourier_Returns403_AndWithoutIdentity401()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        var body = new { url = s.Merchant.UrlFor("a") };

        using var courier = s.ClientAs(DevUsers.Courier(Guid.CreateVersion7()));
        using var anonymous = s.Api.CreateClient();

        (await courier.PutAsJsonAsync("/api/v1/webhook-subscription", body, Cancel)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await anonymous.PutAsJsonAsync("/api/v1/webhook-subscription", body, Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RotateSecret_GivesANewSecret_AndTheOldOneStopsVerifying()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        var oldSecret = await s.SubscribeAsync("m-1", "a");
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PostAsync("/api/v1/webhook-subscription/rotate-secret", content: null, Cancel);
        var newSecret = (await response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("secret").GetString()!;
        s.Merchant.SetSecret("a", oldSecret); // the merchant has not updated its configuration yet
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        await s.RunRoundAsync(Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        newSecret.ShouldNotBe(oldSecret);
        s.Merchant.ReceivedOn("a").ShouldHaveSingleItem().SignatureValid.ShouldBeFalse("signed with the new secret, which the old one cannot verify");
    }

    [Fact]
    public async Task RotateSecret_WithoutASubscription_Returns404()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PostAsync("/api/v1/webhook-subscription/rotate-secret", content: null, Cancel);

        await ProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    // ---- Delivery history ------------------------------------------------------------------------------------------------

    private static async Task<List<WebhookDelivery>> MakeDeadAsync(NotificationsScenario s, string merchantId, int count)
    {
        s.Merchant.Behavior("a").StatusCode = 500;
        for (var i = 0; i < count; i++)
        {
            await s.OrderStatusChangedAsync(merchantId, "Delivered", orderVersion: i + 2);
        }

        for (var round = 0; round < 8; round++)
        {
            await s.AdvanceAndRunAsync(TimeSpan.FromMinutes(31), 1, Cancel);
        }

        return await s.DeliveriesAsync();
    }

    [Fact]
    public async Task List_ShowsOnlyTheCallersOwnDeliveries_AndFiltersByStatus()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        await s.SubscribeAsync("m-2", "b");
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        await s.OrderStatusChangedAsync("m-2", "Delivered");
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/webhook-deliveries", Cancel);
        var dead = await client.GetFromJsonAsync<JsonElement>("/api/v1/webhook-deliveries?status=Dead", Cancel);

        all.GetProperty("items").GetArrayLength().ShouldBe(1, "m-2's delivery is not m-1's business");
        all.GetProperty("items")[0].GetProperty("status").GetString().ShouldBe("Pending");
        dead.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task List_IsPagedNewestFirst_WithACursor()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        for (var i = 0; i < 5; i++)
        {
            s.Fake.Advance(TimeSpan.FromSeconds(1)); // ids are time-ordered: distinct seconds give a clear order
            await s.OrderStatusChangedAsync("m-1", "Delivered", orderVersion: i + 2);
        }

        using var client = s.ClientAs(DevUsers.Merchant("m-1"));
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/webhook-deliveries?limit=2" + (cursor is null ? string.Empty : $"&after={cursor}");
            var page = await client.GetFromJsonAsync<JsonElement>(url, Cancel);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        seen.Count.ShouldBe(5);
        seen.Distinct().Count().ShouldBe(5);
        seen.ShouldBe([.. seen.OrderByDescending(x => x)], "newest first");
    }

    [Theory]
    [InlineData("status=Sleeping")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("after=not-a-cursor")]
    public async Task List_WithBadParameters_Returns400(string query)
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.GetAsync($"/api/v1/webhook-deliveries?{query}", Cancel);

        await ProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    // ---- Replay ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Replay_OfADeadDelivery_Returns202_AndTheDeliveryIsSentAgain()
    {
        // F07 PASS 5: attempt limit reached -> Dead; after the replay the delivery goes out.
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var dead = (await MakeDeadAsync(s, "m-1", 1)).ShouldHaveSingleItem();
        dead.Status.ShouldBe(DeliveryStatus.Dead);
        dead.AttemptCount.ShouldBe(7);
        s.Merchant.Behavior("a").StatusCode = 200; // the merchant fixed its endpoint
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PostAsync($"/api/v1/webhook-deliveries/{dead.Id}/replay", content: null, Cancel);
        await s.RunRoundAsync(Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var after = await s.DeliveryAsync(dead.Id);
        after.Status.ShouldBe(DeliveryStatus.Succeeded);
        after.AttemptCount.ShouldBe(1, "a replay is a fresh start");
    }

    [Fact]
    public async Task Replay_OfADeliveryThatIsNotDead_Returns409()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        await s.OrderStatusChangedAsync("m-1", "Delivered");
        var pending = (await s.DeliveriesAsync()).ShouldHaveSingleItem();
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));

        using var response = await client.PostAsync($"/api/v1/webhook-deliveries/{pending.Id}/replay", content: null, Cancel);

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict, "invalid-delivery-state");
        problem.GetProperty("currentStatus").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task Replay_OfAnotherMerchantsDelivery_Returns404AndLeavesItDead()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var dead = (await MakeDeadAsync(s, "m-1", 1)).ShouldHaveSingleItem();
        using var stranger = s.ClientAs(DevUsers.Merchant("m-2"));

        using var response = await stranger.PostAsync($"/api/v1/webhook-deliveries/{dead.Id}/replay", content: null, Cancel);

        await ProblemAsync(response, HttpStatusCode.NotFound, "not-found");
        (await s.DeliveryAsync(dead.Id)).Status.ShouldBe(DeliveryStatus.Dead);
    }

    [Fact]
    public async Task Replay_TwiceAtOnce_ChangesItOnce()
    {
        await using var s = await NotificationsScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SubscribeAsync("m-1", "a");
        var dead = (await MakeDeadAsync(s, "m-1", 1)).ShouldHaveSingleItem();
        using var client = s.ClientAs(DevUsers.Merchant("m-1"));
        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            using var response = await client.PostAsync($"/api/v1/webhook-deliveries/{dead.Id}/replay", content: null, Cancel);
            return response.StatusCode;
        })).ToList();
        gate.SetResult();

        var statuses = await Task.WhenAll(calls);

        statuses.Count(c => c == HttpStatusCode.Accepted).ShouldBe(1);
        statuses.Count(c => c == HttpStatusCode.Conflict).ShouldBe(5);
    }
}
