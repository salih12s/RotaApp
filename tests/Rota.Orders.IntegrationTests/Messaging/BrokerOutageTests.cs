extern alias DispatchApi;

using Microsoft.EntityFrameworkCore;
using Rota.Orders.Api.Domain;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>
/// R-7: the broker goes down while orders keep arriving. The outbox is the buffer; nothing may be lost.
/// Own broker with a fixed port, because it is stopped and started again.
/// </summary>
[Trait("Category", "Messaging")]
public sealed class BrokerOutageTests(OrdersDatabaseFixture databases)
{
    private const int OrderCount = 50;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outage_FiftyOrdersCreatedWhileBrokerIsDown_AllReachDispatchingAfterRestart_NothingLost()
    {
        await using var broker = await Broker.StartAsync(fixedPort: true, Token);
        await using var scenario = await MessagingScenario.StartAsync(
            databases, broker, startOrders: true, startDispatch: true, ScenarioTimings.Fast, Token);

        // The whole chain works first (queues exist, connections are open).
        var warmUp = await scenario.CreateOrderAsync(Rota.TestKit.DevUsers.NewMerchantId(), null, Token);
        await WaitForDispatchingAsync(scenario, [warmUp], "warm-up order");

        await broker.StopAsync(Token);

        // Orders accepts requests with the broker down: the event waits in the outbox, in the same
        // transaction as the order (this is exactly what the outbox pattern is for).
        var created = new List<Guid>();
        for (var i = 0; i < OrderCount; i++)
        {
            created.Add(await scenario.CreateOrderAsync(Rota.TestKit.DevUsers.NewMerchantId(), null, Token));
        }

        await using (var db = scenario.OrdersDb())
        {
            var waiting = await db.OutboxMessages.Where(m => m.ProcessedAt == null && m.Type == "OrderCreated.v1").CountAsync(Token);
            waiting.ShouldBe(OrderCount, "every OrderCreated is still in the outbox, none lost, none published into the void");
        }

        await broker.StartAgainAsync(Token);

        await WaitForDispatchingAsync(scenario, created, $"all {OrderCount} orders");

        // Exactly one job per order (no duplicates from the republishing), and the outbox drains.
        await using var dispatchDb = scenario.DispatchDb();
        (await dispatchDb.DispatchJobs.CountAsync(Token)).ShouldBe(OrderCount + 1);
        await using var ordersDb = scenario.OrdersDb();
        (await ordersDb.Orders.CountAsync(o => created.Contains(o.Id) && o.Version == 2, Token)).ShouldBe(OrderCount);
        await Eventually.UntilAsync(
            async () => await ordersDb.OutboxMessages.CountAsync(m => m.ProcessedAt == null, Token) == 0,
            TimeSpan.FromSeconds(30), "outbox drained", Token);
    }

    private static Task WaitForDispatchingAsync(MessagingScenario scenario, List<Guid> orderIds, string what) =>
        Eventually.UntilAsync(async () =>
        {
            await using var db = scenario.OrdersDb();
            return await db.Orders.CountAsync(o => orderIds.Contains(o.Id) && o.Status == OrderStatus.Dispatching, Token) == orderIds.Count;
        }, TimeSpan.FromSeconds(90), $"{what} Dispatching", Token);
}
