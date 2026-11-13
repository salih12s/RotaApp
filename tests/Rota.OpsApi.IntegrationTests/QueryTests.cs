using System.Text.Json;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.Features.Consumers;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>The dispatcher's GraphQL reads (docs/05 §5) over a real database filled through the real consumers.</summary>
public sealed class QueryTests(OpsFixture fixture)
{
    private const string Dispatcher = "dispatcher:d-1";
    private static readonly DateTimeOffset T0 = TestClock.Start;

    private OpsApiFactory? _shared;

    private OpsApiFactory Api => _shared ??= new OpsApiFactory(fixture.ConnectionString);

    private async Task<Guid> SeedOrderAsync(string merchant, int minute, string? status = null, int? assignedAfterMinutes = null)
    {
        var id = Guid.CreateVersion7();
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateDbContext();
        var created = new OrderCreatedV1(id, merchant, $"PZ-{minute}", new LocationV1(40.99, 29.02, "A"), new LocationV1(40.98, 29.03, "B"), 900, T0.AddMinutes(minute), 1);
        await new OrderCreatedHandler(db).HandleAsync(new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), "t", T0, id, 1, created), ct);

        if (status is not null)
        {
            var changedAt = T0.AddMinutes(minute + (assignedAfterMinutes ?? 1));
            var change = new OrderStatusChangedV1(id, merchant, $"PZ-{minute}", null, status, Guid.CreateVersion7(), null, changedAt, 2);
            await new OrderStatusChangedHandler(db).HandleAsync(new EventEnvelope<OrderStatusChangedV1>(Guid.CreateVersion7(), "t", changedAt, id, 2, change), ct);
        }

        return id;
    }

    private static async Task SeedCourierAsync(string connection, Guid id, string state)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Rota.OpsApi.Infrastructure.OpsDbContext>();
        Rota.BuildingBlocks.Persistence.PersistenceExtensions.UseRotaNpgsql(options, connection);
        await using var db = new Rota.OpsApi.Infrastructure.OpsDbContext(options.Options);
        var data = new CourierStateChangedV1(id, $"Kurye {id.ToString("N")[..4]}", state, null, T0, 1);
        await new CourierStateChangedHandler(db).HandleAsync(new EventEnvelope<CourierStateChangedV1>(Guid.CreateVersion7(), "t", T0, id, 1, data), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Without_a_token_the_order_list_is_refused()
    {
        var body = await Api.QueryAsync("{ orders { nodes { id } } }", null, devUser: null, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("AUTH_NOT_AUTHENTICATED");
    }

    [Theory]
    [InlineData("merchant:merchant-x")]
    [InlineData("courier:11111111-1111-4111-8111-111111111111")]
    public async Task Only_the_dispatcher_role_may_read(string devUser)
    {
        var body = await Api.QueryAsync("{ dispatchOverview { createdOrders } }", null, devUser, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("AUTH_NOT_AUTHORIZED");
    }

    [Fact]
    public async Task Orders_page_newest_first_without_overlap_or_gaps()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchant = $"merchant-{Guid.CreateVersion7():N}";
        var ids = new List<Guid>();
        for (var minute = 1; minute <= 5; minute++)
        {
            ids.Add(await SeedOrderAsync(merchant, minute));
        }

        const string query = """
            query($m: String!, $after: String) {
              orders(where: { merchantId: { eq: $m } }, first: 2, after: $after) {
                nodes { id } pageInfo { hasNextPage endCursor }
              }
            }
            """;

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var data = (await Api.QueryAsync(query, new { m = merchant, after = cursor }, Dispatcher, ct)).GetProperty("data").GetProperty("orders");
            seen.AddRange(data.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()));
            var info = data.GetProperty("pageInfo");
            cursor = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3, "5 orders in pages of 2");
        seen.ShouldBe([.. ids.AsEnumerable().Reverse()], "newest first, every order exactly once");
    }

    [Fact]
    public async Task Orders_can_be_filtered_by_status_and_sorted()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchant = $"merchant-{Guid.CreateVersion7():N}";
        await SeedOrderAsync(merchant, 1, "Unassigned");
        var assignedEarly = await SeedOrderAsync(merchant, 2, "Assigned");
        var assignedLate = await SeedOrderAsync(merchant, 3, "Assigned");

        var body = await Api.QueryAsync("""
            query($m: String!) {
              orders(where: { merchantId: { eq: $m }, status: { eq: ASSIGNED } }, order: [{ createdAt: ASC }]) { nodes { id status } }
            }
            """, new { m = merchant }, Dispatcher, ct);

        body.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        var nodes = body.GetProperty("data").GetProperty("orders").GetProperty("nodes").EnumerateArray().ToList();
        nodes.Select(n => n.GetProperty("id").GetGuid()).ShouldBe([assignedEarly, assignedLate]);
        nodes.ShouldAllBe(n => n.GetProperty("status").GetString() == "ASSIGNED");
    }

    [Fact]
    public async Task One_order_has_its_places_and_how_long_it_took_to_assign()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await SeedOrderAsync($"merchant-{Guid.CreateVersion7():N}", 1, "Assigned", assignedAfterMinutes: 4);

        var body = await Api.QueryAsync("""
            query($id: UUID!) { order(id: $id) { id status pickup { lat lon } dropoff { lat } timeToAssignSeconds } }
            """, new { id }, Dispatcher, ct);

        var order = body.GetProperty("data").GetProperty("order");
        order.GetProperty("status").GetString().ShouldBe("ASSIGNED");
        order.GetProperty("pickup").GetProperty("lat").GetDouble().ShouldBe(40.99);
        order.GetProperty("dropoff").GetProperty("lat").GetDouble().ShouldBe(40.98);
        order.GetProperty("timeToAssignSeconds").GetInt32().ShouldBe(240);
    }

    [Fact]
    public async Task An_unknown_order_is_null_not_an_error()
    {
        var body = await Api.QueryAsync("query($id: UUID!) { order(id: $id) { id } }", new { id = Guid.CreateVersion7() }, Dispatcher, TestContext.Current.CancellationToken);

        body.GetProperty("data").GetProperty("order").ValueKind.ShouldBe(JsonValueKind.Null);
        body.TryGetProperty("errors", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Couriers_can_be_filtered_by_state()
    {
        var available = Guid.CreateVersion7();
        await SeedCourierAsync(fixture.ConnectionString, available, "Available");
        await SeedCourierAsync(fixture.ConnectionString, Guid.CreateVersion7(), "OnDelivery");

        var body = await Api.QueryAsync("{ couriers(state: AVAILABLE) { id state displayName } }", null, Dispatcher, TestContext.Current.CancellationToken);

        var couriers = body.GetProperty("data").GetProperty("couriers").EnumerateArray().ToList();
        couriers.Select(c => c.GetProperty("id").GetGuid()).ShouldContain(available);
        couriers.ShouldAllBe(c => c.GetProperty("state").GetString() == "AVAILABLE");
    }

    [Fact]
    public async Task The_overview_counts_what_the_dispatcher_needs_to_act_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await fixture.CreateDatabaseAsync(ct); // its own database: it counts everything in it
        await using var api = new OpsApiFactory(connection);

        async Task SeedAsync(string status)
        {
            var id = Guid.CreateVersion7();
            var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Rota.OpsApi.Infrastructure.OpsDbContext>();
            Rota.BuildingBlocks.Persistence.PersistenceExtensions.UseRotaNpgsql(options, connection);
            await using var db = new Rota.OpsApi.Infrastructure.OpsDbContext(options.Options);
            var created = new OrderCreatedV1(id, "m", "r", new LocationV1(1, 1, "a"), new LocationV1(2, 2, "b"), 1, T0, 1);
            await new OrderCreatedHandler(db).HandleAsync(new EventEnvelope<OrderCreatedV1>(Guid.CreateVersion7(), "t", T0, id, 1, created), ct);
            if (status != "Created")
            {
                var change = new OrderStatusChangedV1(id, "m", "r", null, status, null, null, T0, 2);
                await new OrderStatusChangedHandler(db).HandleAsync(new EventEnvelope<OrderStatusChangedV1>(Guid.CreateVersion7(), "t", T0, id, 2, change), ct);
            }
        }

        foreach (var status in new[] { "Created", "Created", "Unassigned", "Assigned", "PickedUp", "Delivered", "Delivered", "Delivered" })
        {
            await SeedAsync(status);
        }

        await SeedCourierAsync(connection, Guid.CreateVersion7(), "Available");
        await SeedCourierAsync(connection, Guid.CreateVersion7(), "Available");
        await SeedCourierAsync(connection, Guid.CreateVersion7(), "OnDelivery");

        var body = await api.QueryAsync("{ dispatchOverview { createdOrders unassignedOrders activeDeliveries availableCouriers } }", null, Dispatcher, ct);

        var overview = body.GetProperty("data").GetProperty("dispatchOverview");
        overview.GetProperty("createdOrders").GetInt32().ShouldBe(2);
        overview.GetProperty("unassignedOrders").GetInt32().ShouldBe(1);
        overview.GetProperty("activeDeliveries").GetInt32().ShouldBe(2);
        overview.GetProperty("availableCouriers").GetInt32().ShouldBe(2);
    }
}
