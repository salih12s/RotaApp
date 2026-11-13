using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rota.OpsApi.GraphQL;
using Rota.OpsApi.Infrastructure;
using StackExchange.Redis;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// Live data over a real WebSocket and a real Redis: the dispatcher's two feeds, the customer's tracking link (R-11, I-9) and the two-instance case (PASS 5).
/// A subscription registers a moment after it is sent and the protocol has no "subscribed" reply, so tests repeat the trigger until the first message
/// arrives (see <see cref="GraphQlSocket.NextWhileAsync"/>) instead of sleeping.
/// </summary>
public sealed class SubscriptionTests(OpsFixture fixture) : IAsyncLifetime
{
    private const string Dispatcher = "dispatcher:d-1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private const string Locations = "subscription($ids: [UUID!]) { courierLocationUpdated(courierIds: $ids) { courierId lat lon } }";
    private const string Statuses = "subscription { orderStatusChanged { orderId toStatus courierId orderVersion } }";
    private const string Tracking = "subscription($t: String!) { orderTracking(trackingToken: $t) { orderId lat lon } }";

    private readonly TrackingKeys _keys = new();
    private readonly FakeHttpServer _orders = new();
    private readonly List<OpsApiFactory> _apis = [];
    private ConnectionMultiplexer _redis = null!;

    public async ValueTask InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        _orders.Respond = (request, _) => Task.FromResult(request.Path == "/.well-known/tracking-jwks"
            ? FakeHttpServer.Json(HttpStatusCode.OK, _keys.Jwks())
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var api in _apis)
        {
            await api.DisposeAsync();
        }

        _keys.Dispose();
        await _redis.DisposeAsync();
    }

    private OpsApiFactory StartApi()
    {
        var api = new OpsApiFactory(
            fixture.ConnectionString,
            configureServices: services => services.AddHttpClient(TrackingTokenValidator.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _orders),
            redis: fixture.RedisConnectionString);
        _apis.Add(api);
        return api;
    }

    private async Task PublishLocationAsync(Guid courierId, double lat) =>
        await _redis.GetSubscriber().PublishAsync(
            RedisChannel.Literal($"trk:courier:{courierId:D}:location"),
            JsonSerializer.Serialize(new { courierId, lat, lon = 29.0, recordedAt = DateTimeOffset.UtcNow, seq = 1 }));

    private static JsonElement Data(JsonElement message, string field) => message.GetProperty("payload").GetProperty("data").GetProperty(field);

    private static string ErrorCodeIn(JsonElement message)
    {
        var text = message.GetRawText();
        return text.Contains("AUTH_NOT_AUTHENTICATED", StringComparison.Ordinal) ? "AUTH_NOT_AUTHENTICATED"
            : text.Contains("AUTH_NOT_AUTHORIZED", StringComparison.Ordinal) ? "AUTH_NOT_AUTHORIZED"
            : text.Contains("invalid-tracking-token", StringComparison.Ordinal) ? "invalid-tracking-token"
            : text.Contains("tracking-not-available", StringComparison.Ordinal) ? "tracking-not-available"
            : text.Contains("dependency-unavailable", StringComparison.Ordinal) ? "dependency-unavailable"
            : $"(no known code) {text}";
    }

    // ── Dispatcher: courier positions ───────────────────────────────

    [Fact]
    public async Task The_dispatcher_receives_a_courier_position_that_Tracking_publishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var courier = Guid.NewGuid();
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), Dispatcher, ct);
        await socket.SubscribeAsync(Locations, new { ids = (Guid[]?)null }, ct);

        var message = await socket.NextWhileAsync(() => PublishLocationAsync(courier, 41.5), Patience, ct);

        var update = Data(message, "courierLocationUpdated");
        update.GetProperty("courierId").GetGuid().ShouldBe(courier);
        update.GetProperty("lat").GetDouble().ShouldBe(41.5);
    }

    [Fact]
    public async Task A_courier_filter_lets_only_the_named_couriers_through()
    {
        var ct = TestContext.Current.CancellationToken;
        var (wanted, other) = (Guid.NewGuid(), Guid.NewGuid());
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), Dispatcher, ct);
        await socket.SubscribeAsync(Locations, new { ids = new[] { wanted } }, ct);

        // The other courier is published first each time: if the filter were missing, its position would arrive first.
        var message = await socket.NextWhileAsync(async () =>
        {
            await PublishLocationAsync(other, 1);
            await PublishLocationAsync(wanted, 2);
        }, Patience, ct);

        Data(message, "courierLocationUpdated").GetProperty("courierId").GetGuid().ShouldBe(wanted);
    }

    // ── Who may subscribe ───────────────────────────────────────────

    [Theory]
    [InlineData(null, "AUTH_NOT_AUTHENTICATED")]
    [InlineData("merchant:merchant-x", "AUTH_NOT_AUTHORIZED")]
    [InlineData("courier:11111111-1111-4111-8111-111111111111", "AUTH_NOT_AUTHORIZED")]
    public async Task The_dispatcher_feeds_refuse_everyone_else(string? devUser, string expectedCode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser, ct);

        await socket.SubscribeAsync(Locations, new { ids = (Guid[]?)null }, ct);
        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe(expectedCode);
        (await socket.NextAsync(Patience, ct)).GetProperty("type").GetString().ShouldBe("complete", "a refused subscription is over, nothing is registered");

        await socket.SubscribeAsync(Statuses, null, ct);
        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe(expectedCode);
        (await socket.NextAsync(Patience, ct)).GetProperty("type").GetString().ShouldBe("complete");
    }

    // ── Dispatcher: order status, on one instance and across two ────

    [Fact]
    public async Task The_dispatcher_hears_an_order_status_change_applied_on_the_same_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "Assigned", ct);
        var api = StartApi();
        await using var socket = await GraphQlSocket.ConnectAsync(api, Dispatcher, ct);
        await socket.SubscribeAsync(Statuses, null, ct);

        var version = 2;
        var message = await socket.NextWhileAsync(() => ChangeOnAsync(api, orderId, "PickedUp", courierId, ++version, ct), Patience, ct);

        var change = Data(message, "orderStatusChanged");
        change.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        change.GetProperty("toStatus").GetString().ShouldBe("PICKED_UP");
    }

    /// <summary>PASS 5: RabbitMQ gives an event to ONE instance (A). A panel connected to the OTHER instance (B) must still see it.</summary>
    [Fact]
    public async Task A_panel_on_instance_B_hears_a_status_change_that_instance_A_consumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "Assigned", ct);
        var (instanceA, instanceB) = (StartApi(), StartApi());
        await using var socketOnB = await GraphQlSocket.ConnectAsync(instanceB, Dispatcher, ct);
        await socketOnB.SubscribeAsync(Statuses, null, ct);

        var version = 2;
        var message = await socketOnB.NextWhileAsync(() => ChangeOnAsync(instanceA, orderId, "PickedUp", courierId, ++version, ct), Patience, ct);

        Data(message, "orderStatusChanged").GetProperty("orderId").GetGuid().ShouldBe(orderId);
    }

    [Fact]
    public async Task An_old_event_that_changes_nothing_is_not_announced()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "PickedUp", ct);
        var api = StartApi();
        await using var socket = await GraphQlSocket.ConnectAsync(api, Dispatcher, ct);
        await socket.SubscribeAsync(Statuses, null, ct);

        // Version 1 is older than the row's version 2: the handler ignores it, so there is nothing to tell the panel.
        // First prove the subscription is live (a newer change does arrive), then send old events and expect silence.
        var version = 2;
        await socket.NextWhileAsync(() => ChangeOnAsync(api, orderId, "PickedUp", courierId, ++version, ct), Patience, ct);
        for (var i = 0; i < 5; i++)
        {
            await ChangeOnAsync(api, orderId, "Assigned", courierId, version: 1, ct);
        }

        // Drain whatever the repeated trigger above may still have queued, then the old events must produce nothing.
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            while (true)
            {
                var message = await socket.NextAsync(TimeSpan.FromSeconds(1.5), ct);
                Data(message, "orderStatusChanged").GetProperty("toStatus").GetString().ShouldBe("PICKED_UP", "an old 'Assigned' must never be announced");
            }
        });
    }

    private static async Task ChangeOnAsync(OpsApiFactory api, Guid orderId, string to, Guid courierId, int version, CancellationToken cancellationToken)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await Seed.ChangeAsync(
            scope.ServiceProvider.GetRequiredService<OpsDbContext>(), orderId, to, courierId, version,
            scope.ServiceProvider.GetRequiredService<OrderStatusNotifier>(), cancellationToken);
    }

    // ── Customer: the tracking link (R-11, I-9) ─────────────────────

    [Fact]
    public async Task A_valid_link_follows_the_orders_own_courier_and_nobody_elses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "Assigned", ct);
        var stranger = Guid.NewGuid();
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);
        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId) }, ct);

        // The stranger's position goes out first each time: the customer must only ever get their own courier's.
        var message = await socket.NextWhileAsync(async () =>
        {
            await PublishLocationAsync(stranger, 10);
            await PublishLocationAsync(courierId, 20);
        }, Patience, ct);

        var update = Data(message, "orderTracking");
        update.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        update.GetProperty("lat").GetDouble().ShouldBe(20);
    }

    [Fact]
    public async Task The_stream_ends_when_the_order_is_delivered_and_later_positions_are_not_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "PickedUp", ct);
        var api = StartApi();
        await using var socket = await GraphQlSocket.ConnectAsync(api, devUser: null, ct);
        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId) }, ct);
        (await socket.NextWhileAsync(() => PublishLocationAsync(courierId, 1), Patience, ct)).GetProperty("type").GetString().ShouldBe("next");

        // Delivery is reported while positions keep coming. A position that was already on its way may still arrive; the stream must then end with "complete".
        var version = 2;
        var deadline = DateTimeOffset.UtcNow + Patience;
        JsonElement message;
        do
        {
            message = await socket.NextWhileAsync(async () =>
            {
                await ChangeOnAsync(api, orderId, "Delivered", courierId, ++version, ct);
                await PublishLocationAsync(courierId, 2);
            }, Patience, ct);

            // Without a bound this loop would spin forever if the stream never ended: a failing test must fail, not hang.
            (DateTimeOffset.UtcNow < deadline).ShouldBeTrue("positions kept coming after the order was Delivered: the stream did not end");
        }
        while (message.GetProperty("type").GetString() == "next");

        message.GetProperty("type").GetString().ShouldBe("complete");
    }

    [Fact]
    public async Task A_link_that_expires_while_watching_ends_the_stream()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "Assigned", ct);
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);
        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId, lifetime: TimeSpan.FromSeconds(2)) }, ct);
        (await socket.NextWhileAsync(() => PublishLocationAsync(courierId, 1), Patience, ct)).GetProperty("type").GetString().ShouldBe("next");

        JsonElement message;
        do
        {
            message = await socket.NextAsync(Patience, ct);
        }
        while (message.GetProperty("type").GetString() == "next");

        message.GetProperty("type").GetString().ShouldBe("complete");
    }

    public static TheoryData<string> BadLinks => new()
    {
        "not-a-token",
        "a.b.c",
        new string('x', 5000),
    };

    [Theory]
    [MemberData(nameof(BadLinks))]
    public async Task A_garbage_link_is_refused(string garbage)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = garbage }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("invalid-tracking-token");
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, _) = await Seed.OrderAsync(fixture, "Assigned", ct);
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        // Expired an hour ago: far beyond the 30 s tolerance.
        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId, lifetime: TimeSpan.FromHours(-1)) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("invalid-tracking-token");
    }

    [Fact]
    public async Task A_link_signed_with_somebody_elses_key_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, _) = await Seed.OrderAsync(fixture, "Assigned", ct);
        using var forger = new TrackingKeys();
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = forger.Token(orderId) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("invalid-tracking-token");
    }

    [Theory]
    [InlineData("rota-api", "rota-orders")]
    [InlineData("rota-tracking", "someone-else")]
    public async Task A_link_with_the_wrong_audience_or_issuer_is_refused(string audience, string issuer)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, _) = await Seed.OrderAsync(fixture, "Assigned", ct);
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId, audience: audience, issuer: issuer) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("invalid-tracking-token");
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Delivered")]
    [InlineData("Cancelled")]
    public async Task A_valid_link_for_an_order_that_cannot_be_followed_is_refused(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, _) = await Seed.OrderAsync(fixture, status, ct);
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("tracking-not-available");
    }

    [Fact]
    public async Task A_valid_link_for_an_order_this_system_does_not_know_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(Guid.NewGuid()) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("tracking-not-available");
    }

    [Fact]
    public async Task The_public_keys_are_fetched_once_and_reused_across_subscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var api = StartApi();

        for (var i = 0; i < 3; i++)
        {
            await using var socket = await GraphQlSocket.ConnectAsync(api, devUser: null, ct);
            await socket.SubscribeAsync(Tracking, new { t = _keys.Token(Guid.NewGuid()) }, ct);
            await socket.NextAsync(Patience, ct);
        }

        _orders.Seen.Count(r => r.Path == "/.well-known/tracking-jwks").ShouldBe(1, "anonymous callers must not make Orders serve its keys on every attempt");
    }

    [Fact]
    public async Task When_Orders_cannot_serve_its_keys_the_customer_sees_an_outage_not_a_verdict_on_the_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, _) = await Seed.OrderAsync(fixture, "Assigned", ct);
        _orders.Respond = (_, _) => throw new HttpRequestException("Orders is down");
        await using var socket = await GraphQlSocket.ConnectAsync(StartApi(), devUser: null, ct);

        await socket.SubscribeAsync(Tracking, new { t = _keys.Token(orderId) }, ct);

        ErrorCodeIn(await socket.NextAsync(Patience, ct)).ShouldBe("dependency-unavailable");
    }
}
