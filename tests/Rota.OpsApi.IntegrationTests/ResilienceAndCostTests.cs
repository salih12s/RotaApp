using System.Text.Json;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// What the Ops API does when Tracking is slow or gone (docs/06 §6: degrade, do not fail) and when a query is too expensive or too deep to run at all
/// (PASS 4: refused before any resolver, database query or gRPC call).
/// </summary>
public sealed class ResilienceAndCostTests(OpsFixture fixture)
{
    private const string Dispatcher = "dispatcher:d-1";

    private const string OrderWithPosition = """
        query($id: UUID!) { order(id: $id) { id courier { id lastLocation { lat lon } } } }
        """;

    private async Task<Guid> SeedOrderWithCourierAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orderId, courierId) = await Seed.OrderAsync(fixture, "Assigned", ct);
        await Seed.CourierAsync(fixture, courierId, orderId, ct);
        return orderId;
    }

    private static void ShouldBeAnswerWithoutPosition(JsonElement body)
    {
        body.TryGetProperty("errors", out var errors).ShouldBeFalse($"a missing position must not fail the query: {errors}");
        var courier = body.GetProperty("data").GetProperty("order").GetProperty("courier");
        courier.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.String);
        courier.GetProperty("lastLocation").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Tracking down or slow: the panel still gets orders and couriers ──

    [Fact]
    public async Task When_Tracking_is_unreachable_the_answer_comes_back_without_positions()
    {
        var orderId = await SeedOrderWithCourierAsync();
        // Nothing listens on port 1: the gRPC call fails at once with UNAVAILABLE.
        await using var api = new OpsApiFactory(fixture.ConnectionString, trackingAddress: "http://127.0.0.1:1");

        var body = await api.QueryAsync(OrderWithPosition, new { id = orderId }, Dispatcher, TestContext.Current.CancellationToken);

        ShouldBeAnswerWithoutPosition(body);
    }

    [Fact]
    public async Task A_Tracking_slower_than_300ms_is_cut_off_and_the_answer_comes_back_without_positions()
    {
        var orderId = await SeedOrderWithCourierAsync();
        await using var tracking = await FakeTrackingServer.StartAsync();
        tracking.State.Delay = TimeSpan.FromSeconds(20);
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address,
            settings: new Dictionary<string, string> { ["Ops:TrackingDeadline"] = "00:00:00.300" });

        var started = DateTimeOffset.UtcNow;
        var body = await api.QueryAsync(OrderWithPosition, new { id = orderId }, Dispatcher, TestContext.Current.CancellationToken);

        ShouldBeAnswerWithoutPosition(body);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "the deadline, not the slow server, decides how long the panel waits");
    }

    [Fact]
    public async Task After_repeated_Tracking_failures_the_circuit_opens_and_no_more_calls_are_made()
    {
        var orderId = await SeedOrderWithCourierAsync();
        await using var tracking = await FakeTrackingServer.StartAsync();
        tracking.State.Delay = TimeSpan.FromSeconds(20);
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address, settings: new Dictionary<string, string>
        {
            ["Ops:TrackingDeadline"] = "00:00:00.200",
            ["Ops:BreakerMinimumThroughput"] = "3",
            ["Ops:BreakerDuration"] = "00:01:00",
        });

        for (var i = 0; i < 3; i++)
        {
            ShouldBeAnswerWithoutPosition(await api.QueryAsync(OrderWithPosition, new { id = orderId }, Dispatcher, TestContext.Current.CancellationToken));
        }

        tracking.State.Batches.Count.ShouldBe(3);

        // Open: the fourth query is answered (still without positions) without bothering Tracking again.
        ShouldBeAnswerWithoutPosition(await api.QueryAsync(OrderWithPosition, new { id = orderId }, Dispatcher, TestContext.Current.CancellationToken));
        tracking.State.Batches.Count.ShouldBe(3);
    }

    // ── PASS 4: cost and depth limits ───────────────────────────────

    [Fact]
    public async Task A_query_over_the_cost_limit_is_refused_before_anything_runs()
    {
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);

        // 200 orders, each with a courier and its position: far above 2500 by Hot Chocolate's count (a list multiplies what is inside it).
        var body = await api.QueryAsync("{ orders(first: 200) { nodes { id courier { id lastLocation { lat lon } } } } }", null, Dispatcher, TestContext.Current.CancellationToken);

        body.TryGetProperty("errors", out var errors).ShouldBeTrue("the expensive query must be refused");
        errors[0].GetProperty("message").GetString()!.ShouldContain("cost", Case.Insensitive);
        body.TryGetProperty("data", out var data).ShouldBe(false, $"nothing may have been resolved, got: {data}");
        tracking.State.Batches.ShouldBeEmpty("a refused query must not reach Tracking");
    }

    [Fact]
    public async Task The_panels_own_50_order_screen_is_still_allowed()
    {
        // Guards the limit from the other side: 2500 must not be so low that the main screen is refused (see docs/05 §5).
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);

        var body = await api.QueryAsync("{ orders(first: 50) { nodes { id courier { id lastLocation { lat lon } } } } }", null, Dispatcher, TestContext.Current.CancellationToken);

        body.TryGetProperty("errors", out var errors).ShouldBeFalse($"unexpected: {errors}");
    }

    [Fact]
    public async Task A_query_nested_deeper_than_the_depth_limit_is_refused()
    {
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);

        // order > courier > currentOrder > courier > ... : a cycle in the schema lets a client nest forever; the limit stops it.
        var query = "{ order(id: \"00000000-0000-0000-0000-000000000001\") { " + string.Concat(Enumerable.Repeat("courier { currentOrder { ", 6)) + "id" + new string('}', 12) + " } }";
        var body = await api.QueryAsync(query, null, Dispatcher, TestContext.Current.CancellationToken);

        body.TryGetProperty("errors", out var errors).ShouldBeTrue("a 13-level query must be refused");
        errors[0].GetProperty("message").GetString()!.ShouldContain("depth", Case.Insensitive);
        tracking.State.Batches.ShouldBeEmpty();
    }
}
