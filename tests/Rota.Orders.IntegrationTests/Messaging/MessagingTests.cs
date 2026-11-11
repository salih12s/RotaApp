extern alias DispatchApi;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DispatchApi::Rota.Dispatch.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Orders.Api.Domain;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>
/// The messaging guarantees, tested against a real RabbitMQ and real PostgreSQL. Mocks cannot show
/// these: redelivery, the inbox primary key and dead-lettering live inside the broker and the database.
/// </summary>
[Trait("Category", "Messaging")]
public sealed class MessagingTests(OrdersDatabaseFixture databases, BrokerFixture brokers)
{
    private const string OrderCreatedQueue = "q.dispatch.order-created";
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- R-6: the same event twice → one side effect ---------------------------------------

    [Fact]
    public async Task Consume_SameEventDeliveredTwice_HasExactlyOneSideEffect()
    {
        await using var scenario = await StartAsync(startOrders: false, startDispatch: true);
        var orderId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var created = OrderCreated(orderId);

        // Redelivery: the broker may hand the same message again (lost ACK, reconnect). Same eventId twice.
        await scenario.Probe.PublishEventAsync(created, eventId, orderId, 1, Token);
        await scenario.Probe.PublishEventAsync(created, eventId, orderId, 1, Token);

        // The queue is processed in order, so once this later, different event is done, both copies are.
        var sentinel = Guid.CreateVersion7();
        await scenario.Probe.PublishEventAsync(OrderCreated(sentinel), Guid.CreateVersion7(), sentinel, 1, Token);
        await WaitForJobAsync(scenario, sentinel);

        await using var db = scenario.DispatchDb();
        (await db.DispatchJobs.CountAsync(j => j.OrderId == orderId, Token)).ShouldBe(1);
        (await OutboxRowsAsync(db, orderId)).Where(m => m.Type == "DispatchStarted.v1").ShouldHaveSingleItem();
        (await db.Set<InboxMessage>().CountAsync(m => m.MessageId == eventId, Token)).ShouldBe(1);
        (await scenario.Probe.MessageCountAsync(Topology.DeadLetterQueue(OrderCreatedQueue), Token)).ShouldBe(0u);
    }

    // ---- Poison message: 3 retries, then DLQ; the queue behind it keeps flowing ------------

    [Fact]
    public async Task Consume_PoisonMessage_IsRetriedThreeTimesThenParkedInDlq_WhileLaterMessagesFlow()
    {
        // Slow enough retries that "the next message was processed" provably happens before the DLQ.
        var timings = new ScenarioTimings([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)]);
        await using var scenario = await StartAsync(startOrders: false, startDispatch: true, timings);
        var dlq = Topology.DeadLetterQueue(OrderCreatedQueue);

        var orderId = Guid.CreateVersion7();
        await scenario.Probe.PublishEventAsync(OrderCreated(orderId), Guid.CreateVersion7(), orderId, 1, Token);
        await WaitForJobAsync(scenario, orderId);

        // Poison: a NEW event (so the inbox lets it in) for an order that already has a job. The handler's
        // INSERT hits the primary key every single time, so retrying can never succeed.
        var poisonEventId = Guid.CreateVersion7();
        await scenario.Probe.PublishEventAsync(OrderCreated(orderId), poisonEventId, orderId, 1, Token);
        var next = Guid.CreateVersion7();
        await scenario.Probe.PublishEventAsync(OrderCreated(next), Guid.CreateVersion7(), next, 1, Token);

        await WaitForJobAsync(scenario, next);
        (await scenario.Probe.MessageCountAsync(dlq, Token)).ShouldBe(0u, "the poison message is still being retried, not parked yet");

        await Eventually.UntilAsync(
            async () => await scenario.Probe.MessageCountAsync(dlq, Token) == 1u, _wait, "poison message reaches the DLQ", Token);

        var parked = (await scenario.Probe.TakeAsync(dlq, Token)).ShouldNotBeNull();
        parked.MessageId.ShouldBe(poisonEventId.ToString("D"));
        parked.Headers[MessageHeaders.Attempt].ShouldBe("4", "1 first run + 3 retries");
        parked.Headers[MessageHeaders.SourceQueue].ShouldBe(OrderCreatedQueue);
        parked.Headers[MessageHeaders.Error].ShouldNotBeNullOrWhiteSpace();

        // Nothing is left in flight, and the failed attempts left no trace behind.
        foreach (var queue in new[] { OrderCreatedQueue, Topology.RetryQueue(OrderCreatedQueue, 1), Topology.RetryQueue(OrderCreatedQueue, 2), Topology.RetryQueue(OrderCreatedQueue, 3) })
        {
            (await scenario.Probe.MessageCountAsync(queue, Token)).ShouldBe(0u, queue);
        }

        await using var db = scenario.DispatchDb();
        (await db.DispatchJobs.CountAsync(j => j.OrderId == orderId, Token)).ShouldBe(1);
        (await db.Set<InboxMessage>().CountAsync(m => m.MessageId == poisonEventId, Token)).ShouldBe(0, "rolled back with the failed handler");
    }

    [Fact]
    public async Task Consume_MalformedMessage_GoesStraightToDlqWithoutRetries()
    {
        await using var scenario = await StartAsync(startOrders: false, startDispatch: true);
        var messageId = Guid.CreateVersion7();

        await scenario.Probe.PublishRawAsync(OrderCreatedV1.RoutingKey, OrderCreatedV1.EventType, messageId, "{ this is not json", Token);

        var dlq = Topology.DeadLetterQueue(OrderCreatedQueue);
        await Eventually.UntilAsync(async () => await scenario.Probe.MessageCountAsync(dlq, Token) == 1u, _wait, "malformed message in DLQ", Token);
        var parked = (await scenario.Probe.TakeAsync(dlq, Token)).ShouldNotBeNull();
        parked.Headers[MessageHeaders.Attempt].ShouldBe("1", "retrying cannot fix bad JSON");
    }

    // ---- The Orders → Dispatch → Orders round trip, and one trace --------------------------

    [Fact]
    public async Task RoundTrip_OrderCreatedThroughDispatch_MovesOrderToDispatching_AsOneTrace()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is MessagingTelemetry.Name or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var scenario = await StartAsync(startOrders: true, startDispatch: true);
        var traceId = ActivityTraceId.CreateRandom();
        var traceParent = $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";

        var orderId = await scenario.CreateOrderAsync(DevUsers.NewMerchantId(), traceParent, Token);

        await Eventually.UntilAsync(
            async () => await scenario.OrderStatusAsync(orderId, Token) == OrderStatus.Dispatching, _wait, "order reaches Dispatching", Token);
        await Eventually.UntilAsync(
            () => Task.FromResult(InTrace(spans, traceId, "q.orders.dispatch-started process").Any()), _wait, "last consumer span finished", Token);

        // One trace id from the HTTP request through every hop (PASS 4).
        var names = spans.Where(a => a.TraceId == traceId).Select(a => a.OperationName).ToHashSet();
        names.ShouldContain("orders.order-created.v1 publish");
        names.ShouldContain("q.dispatch.order-created process");
        names.ShouldContain("dispatch.dispatch-started.v1 publish");
        names.ShouldContain("q.orders.dispatch-started process");
        spans.Where(a => a.TraceId == traceId && a.Source.Name == "Microsoft.AspNetCore").ShouldNotBeEmpty("the HTTP request is the root of the trace");

        // And it did what it should: a Searching job in Dispatch, version 2 and the status event in Orders.
        await using var dispatchDb = scenario.DispatchDb();
        (await dispatchDb.DispatchJobs.SingleAsync(j => j.OrderId == orderId, Token)).Status.ShouldBe(DispatchJobStatus.Searching);
        await using var ordersDb = scenario.OrdersDb();
        var order = await ordersDb.Orders.Include(o => o.History).SingleAsync(o => o.Id == orderId, Token);
        order.Version.ShouldBe(2);
        order.History.Select(h => h.To).ShouldBe([OrderStatus.Created, OrderStatus.Dispatching], ignoreOrder: true);
        (await OutboxRowsAsync(ordersDb, orderId)).Select(m => m.Type).ShouldBe(["OrderCreated.v1", "OrderStatusChanged.v1"], ignoreOrder: true);
    }

    // ---- Events nobody consumes yet are parked, not lost -----------------------------------

    [Fact]
    public async Task Publish_EventWithNoConsumerQueue_IsParkedInUnroutedQueue_AndOutboxRowIsDone()
    {
        await using var scenario = await StartAsync(startOrders: true, startDispatch: false);

        var orderId = await scenario.CreateOrderAsync(DevUsers.NewMerchantId(), null, Token);

        await Eventually.UntilAsync(
            async () => await scenario.Probe.MessageCountAsync(Topology.UnroutedQueue, Token) >= 1u, _wait, "OrderCreated parked", Token);
        await Eventually.UntilAsync(async () =>
        {
            await using var db = scenario.OrdersDb();
            return (await OutboxRowsAsync(db, orderId)).All(m => m.ProcessedAt is not null);
        }, _wait, "outbox row marked processed", Token);
    }

    // ---- A valid event the order no longer allows: acknowledged, ignored, no retry ---------

    [Fact]
    public async Task Consume_DispatchStartedForCancelledOrder_IsIgnoredWithoutRetryOrDlq()
    {
        await using var scenario = await StartAsync(startOrders: true, startDispatch: false);
        var merchantId = DevUsers.NewMerchantId();
        var cancelled = await scenario.CreateOrderAsync(merchantId, null, Token);
        using (var client = scenario.Orders!.CreateClientAs(DevUsers.Merchant(merchantId)))
        using (var response = await client.PostAsync(new Uri($"/api/v1/orders/{cancelled}/cancel", UriKind.Relative), null, Token))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var live = await scenario.CreateOrderAsync(DevUsers.NewMerchantId(), null, Token);
        await scenario.Probe.PublishEventAsync(new DispatchStartedV1(cancelled, TestClock.Start), Guid.CreateVersion7(), cancelled, 1, Token);
        await scenario.Probe.PublishEventAsync(new DispatchStartedV1(live, TestClock.Start), Guid.CreateVersion7(), live, 1, Token);

        // Queue order: once the later event took effect, the earlier one has been handled too.
        await Eventually.UntilAsync(
            async () => await scenario.OrderStatusAsync(live, Token) == OrderStatus.Dispatching, _wait, "second event applied", Token);

        (await scenario.OrderStatusAsync(cancelled, Token)).ShouldBe(OrderStatus.Cancelled);
        await using var db = scenario.OrdersDb();
        (await OutboxRowsAsync(db, cancelled)).Select(m => m.Type).ShouldBe(["OrderCreated.v1", "OrderStatusChanged.v1"], ignoreOrder: true, "only create + cancel; no Dispatching event");
        (await scenario.Probe.MessageCountAsync(Topology.DeadLetterQueue("q.orders.dispatch-started"), Token)).ShouldBe(0u);
        (await scenario.Probe.MessageCountAsync(Topology.RetryQueue("q.orders.dispatch-started", 1), Token)).ShouldBe(0u);
    }

    private Task<MessagingScenario> StartAsync(bool startOrders, bool startDispatch, ScenarioTimings? timings = null) =>
        StartCoreAsync(startOrders, startDispatch, timings ?? ScenarioTimings.Fast);

    private async Task<MessagingScenario> StartCoreAsync(bool startOrders, bool startDispatch, ScenarioTimings timings) =>
        await MessagingScenario.StartAsync(databases, await brokers.GetAsync(), startOrders, startDispatch, timings, Token);

    private static OrderCreatedV1 OrderCreated(Guid orderId) => new(
        orderId, "merchant-x", $"ref-{orderId:N}",
        new LocationV1(40.9903, 29.029, "Kadıköy"), new LocationV1(40.985, 29.04, "Moda"),
        1120, TestClock.Start, 1);

    private static IEnumerable<Activity> InTrace(IEnumerable<Activity> spans, ActivityTraceId traceId, string name) =>
        spans.Where(a => a.TraceId == traceId && a.OperationName == name);

    private static Task WaitForJobAsync(MessagingScenario scenario, Guid orderId) =>
        Eventually.UntilAsync(async () =>
        {
            await using var db = scenario.DispatchDb();
            return await db.DispatchJobs.AnyAsync(j => j.OrderId == orderId, Token);
        }, _wait, $"dispatch job for {orderId} exists", Token);

    private static Task<List<OutboxMessage>> OutboxRowsAsync(DbContext db, Guid orderId) =>
        db.Set<OutboxMessage>()
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'aggregateId' = {orderId.ToString()}")
            .ToListAsync(Token);
}
