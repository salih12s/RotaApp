extern alias DispatchApi;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DispatchApi::Rota.Dispatch.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Persistence;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Features.CreateOrder;
using Rota.Orders.Api.Infrastructure;
using Rota.Orders.IntegrationTests.Support;
using Rota.TestKit;
using Rota.TestKit.Postgres;

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>How fast the messaging loops run in a test. Production defaults are seconds; tests want milliseconds.</summary>
public sealed record ScenarioTimings(TimeSpan[] RetryDelays)
{
    public static ScenarioTimings Fast { get; } =
        new([TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(900)]);
}

/// <summary>
/// The Orders and Dispatch services running side by side in memory, each with its own fresh database,
/// connected through a real RabbitMQ (own virtual host). Everything a messaging test needs, one per test.
/// </summary>
public sealed class MessagingScenario : IAsyncDisposable
{
    private MessagingScenario(string brokerUri, string ordersConnection, string dispatchConnection, BrokerProbe probe)
    {
        BrokerUri = brokerUri;
        OrdersConnection = ordersConnection;
        DispatchConnection = dispatchConnection;
        Probe = probe;
    }

    public string BrokerUri { get; }

    public string OrdersConnection { get; }

    public string DispatchConnection { get; }

    public BrokerProbe Probe { get; }

    public OrdersApiFactory? Orders { get; private set; }

    public DispatchApiFactory? Dispatch { get; private set; }

    public static async Task<MessagingScenario> StartAsync(
        OrdersDatabaseFixture databases,
        Broker broker,
        bool startOrders,
        bool startDispatch,
        ScenarioTimings timings,
        CancellationToken cancellationToken)
    {
        var brokerUri = await broker.CreateVirtualHostAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var ordersConnection = await databases.Container.CreateDatabaseAsync($"orders_{id}", cancellationToken);
        var dispatchConnection = await databases.Container.CreateDatabaseAsync($"dispatch_{id}", cancellationToken);

        await using (var ordersDb = OrdersDatabaseFixture.CreateDbContext(ordersConnection))
        {
            await ordersDb.Database.MigrateAsync(cancellationToken);
        }

        await using (var dispatchDb = CreateDispatchDb(dispatchConnection))
        {
            await dispatchDb.Database.MigrateAsync(cancellationToken);
        }

        var probe = await BrokerProbe.ConnectAsync(brokerUri, cancellationToken);
        var scenario = new MessagingScenario(brokerUri, ordersConnection, dispatchConnection, probe);

        var settings = SettingsFor(brokerUri, timings);
        if (startOrders)
        {
            scenario.Orders = new OrdersApiFactory(ordersConnection, extraSettings: settings);
            _ = scenario.Orders.Server; // builds and starts the host, including relay and consumer
        }

        if (startDispatch)
        {
            scenario.Dispatch = new DispatchApiFactory(dispatchConnection, settings);
            _ = scenario.Dispatch.Server;
        }

        // Queues are declared by their consumers when they start. An event published before that has no
        // queue to go to and is parked in rota.unrouted, so a test must not publish before the topology
        // exists. (The DLQ is declared last, so its existence means the whole topology is ready.)
        if (startDispatch)
        {
            await scenario.WaitForTopologyAsync("q.dispatch.order-created", cancellationToken);
        }

        if (startOrders)
        {
            await scenario.WaitForTopologyAsync("q.orders.dispatch-started", cancellationToken);
        }

        return scenario;
    }

    private Task WaitForTopologyAsync(string queue, CancellationToken cancellationToken) =>
        Eventually.UntilAsync(
            async () => await Probe.MessageCountAsync(Rota.BuildingBlocks.Messaging.Topology.DeadLetterQueue(queue), cancellationToken) is not null,
            TimeSpan.FromSeconds(30), $"consumer topology of {queue} declared", cancellationToken);

    public static Dictionary<string, string> SettingsFor(string brokerUri, ScenarioTimings timings)
    {
        ArgumentNullException.ThrowIfNull(timings);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Messaging:Enabled"] = "true",
            ["ConnectionStrings:RabbitMq"] = brokerUri,
            ["Messaging:RelayPollInterval"] = "00:00:00.050",
            ["Messaging:ReconnectDelay"] = "00:00:00.300",
        };
        for (var i = 0; i < timings.RetryDelays.Length; i++)
        {
            settings[$"Messaging:RetryDelays:{i}"] = timings.RetryDelays[i].ToString("c", CultureInfo.InvariantCulture);
        }

        return settings;
    }

    public static DispatchDbContext CreateDispatchDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>();
        options.UseRotaNpgsql(connectionString);
        return new DispatchDbContext(options.Options);
    }

    public OrdersDbContext OrdersDb() => OrdersDatabaseFixture.CreateDbContext(OrdersConnection);

    public DispatchDbContext DispatchDb() => CreateDispatchDb(DispatchConnection);

    /// <summary>Creates an order through the real HTTP endpoint, as a merchant would.</summary>
    public async Task<Guid> CreateOrderAsync(string merchantId, string? traceParent, CancellationToken cancellationToken)
    {
        using var client = Orders!.CreateClientAs(DevUsers.Merchant(merchantId));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(OrderRequests.Valid()) };
        request.Headers.Add(CreateOrderEndpoint.IdempotencyKeyHeader, OrderRequests.NewKey());
        if (traceParent is not null)
        {
            request.Headers.Add("traceparent", traceParent);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));
        return (await response.Content.ReadFromJsonAsync<CreateOrderResponse>(cancellationToken))!.Id;
    }

    public async Task<OrderStatus> OrderStatusAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var db = OrdersDb();
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Orders is not null)
        {
            await Orders.DisposeAsync();
        }

        if (Dispatch is not null)
        {
            await Dispatch.DisposeAsync();
        }

        await Probe.DisposeAsync();
    }
}

/// <summary>The Dispatch API in memory, wired to a test database and broker.</summary>
public sealed class DispatchApiFactory(string connectionString, IReadOnlyDictionary<string, string> settings)
    : WebApplicationFactory<DispatchApi::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dispatch", connectionString);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
