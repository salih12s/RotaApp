using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.Features.Consumers;
using Rota.OpsApi.Infrastructure;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// R-12, the N+1 problem: a query that walks from many orders to their couriers and the couriers' positions must cost a fixed number of
/// database queries and gRPC calls, not one per order. The numbers are counted for real: EF commands, and calls the fake Tracking received.
/// </summary>
public sealed class DataLoaderTests(OpsFixture fixture)
{
    private const string Dispatcher = "dispatcher:d-1";
    private static readonly DateTimeOffset T0 = TestClock.Start;

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }

    /// <summary>Seeds <paramref name="count"/> orders of one merchant, each assigned to its own courier who is on delivery of it.</summary>
    private async Task<(string Merchant, List<Guid> Couriers, List<Guid> Orders)> SeedAsync(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var merchant = $"merchant-{Guid.CreateVersion7():N}";
        List<Guid> couriers = [];
        List<Guid> orders = [];

        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var order = Guid.CreateVersion7();
            var courier = Guid.CreateVersion7();
            var at = T0.AddMinutes(i);
            await new OrderCreatedHandler(db).HandleAsync(new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), "t", at, order, 1,
                new OrderCreatedV1(order, merchant, $"PZ-{i}", new LocationV1(1, 1, "a"), new LocationV1(2, 2, "b"), 1, at, 1)), ct);
            await new OrderStatusChangedHandler(db).HandleAsync(new EventEnvelope<OrderStatusChangedV1>(Guid.CreateVersion7(), "t", at, order, 2,
                new OrderStatusChangedV1(order, merchant, $"PZ-{i}", null, "Assigned", courier, null, at, 2)), ct);
            await new CourierStateChangedHandler(db).HandleAsync(new EventEnvelope<CourierStateChangedV1>(Guid.CreateVersion7(), "t", at, courier, 1,
                new CourierStateChangedV1(courier, $"Kurye {i}", "OnDelivery", order, at, 1)), ct);
            couriers.Add(courier);
            orders.Add(order);
        }

        return (merchant, couriers, orders);
    }

    [Fact]
    public async Task Fifty_orders_with_their_couriers_and_positions_cost_two_queries_and_one_gRPC_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tracking = await FakeTrackingServer.StartAsync();
        var counter = new CommandCounter();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address,
            services => services.ConfigureDbContext<OpsDbContext>(options => options.AddInterceptors(counter)));
        var (merchant, couriers, _) = await SeedAsync(50);

        var before = counter.Count;
        var body = await api.QueryAsync("""
            query($m: String!) {
              orders(where: { merchantId: { eq: $m } }, first: 50) {
                nodes { id courier { id displayName lastLocation { lat lon } } }
              }
            }
            """, new { m = merchant }, Dispatcher, ct);

        body.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        var nodes = body.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().ToList();
        nodes.Count.ShouldBe(50);
        nodes.Select(n => n.GetProperty("courier").GetProperty("id").GetGuid()).ShouldBe(couriers.AsEnumerable().Reverse(), ignoreOrder: true);
        nodes.ShouldAllBe(n => n.GetProperty("courier").GetProperty("lastLocation").GetProperty("lat").GetDouble() == 41.0);

        (counter.Count - before).ShouldBe(2, "1 query for the order page + 1 for all 50 couriers, not 51");
        tracking.State.Batches.ShouldBe([50], "one gRPC call that asks for all 50 couriers");
    }

    [Fact]
    public async Task A_courier_without_a_position_has_a_null_location_and_does_not_break_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);
        var (merchant, couriers, _) = await SeedAsync(3);
        tracking.State.Unknown.Add(couriers[1]);

        var body = await api.QueryAsync("""
            query($m: String!) { orders(where: { merchantId: { eq: $m } }) { nodes { courier { id lastLocation { lat } } } } }
            """, new { m = merchant }, Dispatcher, ct);

        body.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        var withoutPosition = body.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("courier").GetProperty("lastLocation").ValueKind == System.Text.Json.JsonValueKind.Null)
            .Select(n => n.GetProperty("courier").GetProperty("id").GetGuid()).ToList();
        withoutPosition.ShouldBe([couriers[1]], "only the courier Tracking does not know has no position; the other two are unaffected");
    }

    [Fact]
    public async Task An_order_nobody_is_assigned_to_has_a_null_courier_and_asks_Tracking_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);
        var merchant = $"merchant-{Guid.CreateVersion7():N}";
        var order = Guid.CreateVersion7();
        await using (var db = fixture.CreateDbContext())
        {
            await new OrderCreatedHandler(db).HandleAsync(new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), "t", T0, order, 1,
                new OrderCreatedV1(order, merchant, "PZ", new LocationV1(1, 1, "a"), new LocationV1(2, 2, "b"), 1, T0, 1)), ct);
        }

        var body = await api.QueryAsync("query($id: UUID!) { order(id: $id) { courier { id } } }", new { id = order }, Dispatcher, ct);

        body.GetProperty("data").GetProperty("order").GetProperty("courier").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        tracking.State.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Couriers_with_their_current_orders_and_positions_are_batched_too()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tracking = await FakeTrackingServer.StartAsync();
        var counter = new CommandCounter();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address,
            services => services.ConfigureDbContext<OpsDbContext>(options => options.AddInterceptors(counter)));
        await SeedAsync(20);

        var body = await api.QueryAsync("{ couriers(state: ON_DELIVERY) { id currentOrder { id status } lastLocation { lat } } }", null, Dispatcher, ct);

        body.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        body.GetProperty("data").GetProperty("couriers").GetArrayLength().ShouldBeGreaterThanOrEqualTo(20);
        counter.Count.ShouldBe(2, "1 query for the couriers + 1 for all their current orders");
        tracking.State.Batches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_Ops_API_calls_Tracking_as_a_service()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var tracking = await FakeTrackingServer.StartAsync();
        await using var api = new OpsApiFactory(fixture.ConnectionString, tracking.Address);
        var (merchant, _, _) = await SeedAsync(1);

        await api.QueryAsync("query($m: String!) { orders(where: { merchantId: { eq: $m } }) { nodes { courier { lastLocation { lat } } } } }", new { m = merchant }, Dispatcher, ct);

        tracking.State.LastCaller.ShouldBe("service:ops-api");
    }
}
