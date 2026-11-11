using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Rota.Orders.Api.Features.CreateOrder;
using Rota.Orders.Api.Infrastructure;
using Rota.Orders.IntegrationTests.Support;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.IntegrationTests.Features;

/// <summary><c>POST /api/v1/orders</c>: idempotency (R-5), atomicity, authorization and input rules.</summary>
public sealed class CreateOrderTests(OrdersDatabaseFixture fixture)
{
    private const int RaceRounds = 20;
    private const int ConcurrentRequests = 10;

    // ---- Riskiest first: concurrency (R-5) -------------------------------------------------

    [Fact]
    public async Task Create_TenConcurrentRequestsWithSameKey_AllGet201WithSameBodyAndOneOrderExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var uniqueViolations = new UniqueViolationCounter();
        await using var api = new OrdersApiFactory(fixture.ConnectionString, s => s.AddSingleton<IInterceptor>(uniqueViolations));

        // One round can get lucky with timing; 20 rounds make a missing guard show up.
        for (var round = 0; round < RaceRounds; round++)
        {
            var merchantId = DevUsers.NewMerchantId();
            using var client = api.CreateClientAs(DevUsers.Merchant(merchantId));
            var request = OrderRequests.Valid();
            var key = OrderRequests.NewKey();

            // A start gate: all requests are created first and released together.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, ConcurrentRequests).Select(async _ =>
            {
                await start.Task;
                using var response = await client.PostOrderAsync(request, key, ct);
                return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(ct));
            }).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(requests);

            results.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created, $"round {round}");
            var body = results.Select(r => r.Body).Distinct(StringComparer.Ordinal).ShouldHaveSingleItem($"round {round}");
            var orderId = JsonSerializer.Deserialize<CreateOrderResponse>(body, JsonSerializerOptions.Web)!.Id;

            await using var db = fixture.CreateDbContext();
            (await db.Orders.CountAsync(o => o.MerchantId == merchantId, ct)).ShouldBe(1, $"round {round}");
            (await CountOutboxAsync(db, orderId, ct)).ShouldBe(1, $"round {round}");
        }

        // Guards against a vacuous pass: at least once, requests really collided on the key.
        uniqueViolations.Count.ShouldBeGreaterThan(0);
    }

    // ---- Atomicity -------------------------------------------------------------------------

    [Fact]
    public async Task Create_WhenFailingAfterOrderInsert_LeavesNoOrderNoOutboxNoKey_AndRetrySucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = new FailOnceAfterOrderInsert();
        await using var api = new OrdersApiFactory(fixture.ConnectionString, s => s.AddSingleton<IInterceptor>(failure));
        var merchantId = DevUsers.NewMerchantId();
        using var client = api.CreateClientAs(DevUsers.Merchant(merchantId));
        var request = OrderRequests.Valid();
        var key = OrderRequests.NewKey();

        using var failed = await client.PostOrderAsync(request, key, ct);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        failure.Fired.ShouldBeTrue();
        await using (var db = fixture.CreateDbContext())
        {
            (await db.Orders.CountAsync(o => o.MerchantId == merchantId, ct)).ShouldBe(0);
            (await db.IdempotencyKeys.CountAsync(k => k.MerchantId == merchantId, ct)).ShouldBe(0);
            (await CountOutboxForMerchantAsync(db, merchantId, ct)).ShouldBe(0);
        }

        // Nothing was left behind, so the client's retry with the same key creates the order.
        using var retried = await client.PostOrderAsync(request, key, ct);

        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using (var db = fixture.CreateDbContext())
        {
            (await db.Orders.CountAsync(o => o.MerchantId == merchantId, ct)).ShouldBe(1);
            (await CountOutboxForMerchantAsync(db, merchantId, ct)).ShouldBe(1);
        }
    }

    // ---- Happy path ------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithValidRequest_Returns201_StoresOrderEventAndOnlyEncryptedSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));
        var request = OrderRequests.Valid();

        using var response = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<CreateOrderResponse>(ct)).ShouldNotBeNull();
        response.Headers.Location.ShouldBe(new Uri($"/api/v1/orders/{body.Id}", UriKind.Relative));
        body.ShouldSatisfyAllConditions(
            b => b.Status.ShouldBe("Created"),
            b => b.OrderVersion.ShouldBe(1),
            b => b.DistanceMeters.ShouldBeInRange(1000, 1200),
            b => b.DeliveryCode.ShouldMatch("^[0-9]{4}$"),
            b => b.TrackingUrl.ShouldStartWith("https://rota.local/track/"));

        await using var db = fixture.CreateDbContext();
        var order = await db.Orders.SingleAsync(o => o.Id == body.Id, ct);
        order.MerchantId.ShouldBe(merchantId);
        OrderTestData.Hasher.Matches(body.DeliveryCode, order.DeliveryCode.Hash).ShouldBeTrue();

        var outbox = await db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'aggregateId' = {body.Id.ToString()}")
            .SingleAsync(ct);
        outbox.Type.ShouldBe("OrderCreated.v1");
        outbox.RoutingKey.ShouldBe("orders.order-created.v1");
        using var envelope = JsonDocument.Parse(outbox.Payload);
        envelope.RootElement.GetProperty("aggregateId").GetGuid().ShouldBe(body.Id);
        envelope.RootElement.GetProperty("data").GetProperty("merchantOrderRef").GetString().ShouldBe(request.MerchantOrderRef);
        outbox.Payload.ShouldNotContain(request.Customer.Phone); // no personal data in events

        // The stored replay copy must not reveal the code (I-5) or the tracking token.
        var stored = await db.IdempotencyKeys.SingleAsync(k => k.MerchantId == merchantId, ct);
        var storedBody = JsonSerializer.Deserialize<CreateOrderResponse>(stored.ResponseBody, JsonSerializerOptions.Web)!;
        storedBody.DeliveryCode.ShouldNotBe(body.DeliveryCode);
        storedBody.TrackingUrl.ShouldNotContain("rota.local");
    }

    [Fact]
    public async Task Create_RetriedWithSameKeyAndBody_ReturnsIdenticalResponseAndOneOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));
        var request = OrderRequests.Valid();
        var key = OrderRequests.NewKey();

        using var first = await client.PostOrderAsync(request, key, ct);
        using var second = await client.PostOrderAsync(request, key, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Content.ReadAsStringAsync(ct)).ShouldBe(await first.Content.ReadAsStringAsync(ct));
        await using var db = fixture.CreateDbContext();
        (await db.Orders.CountAsync(o => o.MerchantId == merchantId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_SameKeyUsedByAnotherMerchant_CreatesSeparateOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = OrderRequests.NewKey();
        var request = OrderRequests.Valid();
        using var merchantA = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        using var merchantB = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var a = await merchantA.PostOrderAsync(request, key, ct);
        using var b = await merchantB.PostOrderAsync(request, key, ct);

        // Keys are scoped per merchant: B must not receive A's order (and A's delivery code).
        a.StatusCode.ShouldBe(HttpStatusCode.Created);
        b.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await b.Content.ReadFromJsonAsync<CreateOrderResponse>(ct))!.Id
            .ShouldNotBe((await a.Content.ReadFromJsonAsync<CreateOrderResponse>(ct))!.Id);
    }

    // ---- Negative: idempotency rules -------------------------------------------------------

    [Fact]
    public async Task Create_SameKeyWithDifferentBody_Returns409IdempotencyKeyReused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var key = OrderRequests.NewKey();
        var request = OrderRequests.Valid();
        using var first = await client.PostOrderAsync(request, key, ct);

        using var second = await client.PostOrderAsync(request with { Dropoff = request.Dropoff with { Address = "Başka adres" } }, key, ct);

        await second.ShouldBeProblemAsync(HttpStatusCode.Conflict, "idempotency-key-reused", ct);
    }

    [Fact]
    public async Task Create_WithoutIdempotencyKey_Returns400IdempotencyKeyMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await client.PostOrderAsync(OrderRequests.Valid(), idempotencyKey: null, ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency-key-missing", ct);
    }

    [Fact]
    public async Task Create_WithTooLongIdempotencyKey_Returns400ValidationFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await client.PostOrderAsync(OrderRequests.Valid(), new string('k', 129), ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", ct);
        problem.GetProperty("errors").TryGetProperty(CreateOrderEndpoint.IdempotencyKeyHeader, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Create_SameMerchantOrderRefWithAnotherKey_Returns409DuplicateMerchantOrderRef()
    {
        var ct = TestContext.Current.CancellationToken;
        var merchantId = DevUsers.NewMerchantId();
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));
        var request = OrderRequests.Valid();
        using var first = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        using var second = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        await second.ShouldBeProblemAsync(HttpStatusCode.Conflict, "duplicate-merchant-order-ref", ct);
        await using var db = fixture.CreateDbContext();
        (await db.IdempotencyKeys.CountAsync(k => k.MerchantId == merchantId, ct)).ShouldBe(1); // the second key rolled back
    }

    // ---- Negative: business rules and input ------------------------------------------------

    [Fact]
    public async Task Create_BeyondServiceArea_Returns422OutOfServiceArea()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var request = OrderRequests.Valid() with { Dropoff = new LocationRequest(39.9208, 32.8541, "Kızılay, Ankara") };

        using var response = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "out-of-service-area", ct);
    }

    [Theory]
    [InlineData(91d, 29d)]
    [InlineData(-90.0001, 29.0)]
    [InlineData(40.99, 180.5)]
    [InlineData(null, 29.0)]
    public async Task Create_WithInvalidCoordinate_Returns400WithFieldErrors(double? lat, double? lon)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var request = OrderRequests.Valid() with { Pickup = new LocationRequest(lat, lon, "Kadıköy") };

        using var response = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", ct);
        problem.GetProperty("errors").EnumerateObject().ShouldContain(e => e.Name.StartsWith("Pickup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_WithInvalidTextFields_Returns400ListingEachField()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var request = OrderRequests.Valid(merchantOrderRef: new string('R', 65)) with
        {
            Customer = new CustomerRequest("", "05551112233"),
        };

        using var response = await client.PostOrderAsync(request, OrderRequests.NewKey(), ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", ct);
        var fields = problem.GetProperty("errors").EnumerateObject().Select(e => e.Name.ToUpperInvariant()).ToList();
        fields.ShouldContain(f => f.Contains("MERCHANTORDERREF", StringComparison.Ordinal));
        fields.ShouldContain(f => f.Contains("NAME", StringComparison.Ordinal));
        fields.ShouldContain(f => f.Contains("PHONE", StringComparison.Ordinal));
    }

    // ---- Negative: authentication and roles ------------------------------------------------

    [Fact]
    public async Task Create_WithoutUser_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClient();

        using var response = await client.PostOrderAsync(OrderRequests.Valid(), OrderRequests.NewKey(), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized", ct);
    }

    [Theory]
    [InlineData("merchant:")]
    [InlineData("courier:not-a-guid")]
    [InlineData("admin:root")]
    public async Task Create_WithMalformedDevUser_Returns401(string devUser)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(devUser);

        using var response = await client.PostOrderAsync(OrderRequests.Valid(), OrderRequests.NewKey(), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_AsCourier_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()));

        using var response = await client.PostOrderAsync(OrderRequests.Valid(), OrderRequests.NewKey(), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", ct);
    }

    private static Task<int> CountOutboxAsync(OrdersDbContext db, Guid orderId, CancellationToken ct) =>
        db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM outbox_messages WHERE payload->>'aggregateId' = {orderId.ToString()}")
            .SingleAsync(ct);

    private static Task<int> CountOutboxForMerchantAsync(OrdersDbContext db, string merchantId, CancellationToken ct) =>
        db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM outbox_messages WHERE payload->'data'->>'merchantId' = {merchantId}")
            .SingleAsync(ct);
}
