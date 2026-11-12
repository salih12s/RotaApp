using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Orders;
using Rota.Contracts.Tracking.V1;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Consumers;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Infrastructure;
using Rota.Dispatch.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests;

public sealed record SeededOffer(Guid OrderId, Guid CourierId, Guid OfferId, DateTimeOffset ExpiresAt);

/// <summary>
/// Everything one test needs, built fresh: its own database, a fake Tracking server and the Dispatch API on a frozen clock.
/// Also the test's hands (seed rows in any state through the real domain methods) and eyes (read rows and the outbox back).
/// </summary>
public sealed class DispatchScenario : IAsyncDisposable
{
    private DispatchScenario(FakeTrackingServer tracking, DispatchApiFactory api)
    {
        Tracking = tracking;
        Api = api;
    }

    public FakeTrackingServer Tracking { get; }

    public DispatchApiFactory Api { get; }

    public FakeTimeProvider Time => Api.Time;

    public static DispatchRules Rules => DispatchRules.Default;

    public static async Task<DispatchScenario> StartAsync(
        DispatchFixture fixture, IReadOnlyDictionary<string, string>? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var tracking = await FakeTrackingServer.StartAsync();
        var connection = await fixture.CreateDatabaseAsync(cancellationToken);
        var api = new DispatchApiFactory(connection, fixture.RedisConnectionString, tracking.Address, settings);
        _ = api.Services; // builds and starts the host
        return new DispatchScenario(tracking, api);
    }

    public AsyncServiceScope Scope() => Api.Services.CreateAsyncScope();

    public async Task<T> QueryAsync<T>(Func<DispatchDbContext, Task<T>> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = Scope();
        return await query(scope.ServiceProvider.GetRequiredService<DispatchDbContext>());
    }

    public async Task<OfferingOutcome> OfferAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<OfferingService>().TryOfferAsync(orderId, cancellationToken);
    }

    /// <summary>What Tracking would report for a courier standing <paramref name="distanceMeters"/> from the pickup, seen just now.</summary>
    public NearbyCourier Near(Guid courierId, int distanceMeters) => new()
    {
        CourierId = courierId.ToString("D"),
        DistanceM = distanceMeters,
        LastSeenAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
    };

    // ---- Seeding: rows built with the real domain methods, so they are states the system can really reach ------------

    public async Task<Guid> SeedCourierAsync(CourierState state = CourierState.Available)
    {
        var now = Time.GetUtcNow();
        var courier = Courier.Register(Guid.CreateVersion7(), "Test Courier", now);
        if (state == CourierState.Available)
        {
            courier.StartShift(now).IsSuccess.ShouldBeTrue();
        }
        else
        {
            state.ShouldBe(CourierState.Offline, "use SeedPendingOfferAsync / SeedOnDeliveryAsync for busy couriers");
        }

        await SaveAsync(db => db.Couriers.Add(courier));
        return courier.Id;
    }

    public async Task<Guid> SeedJobAsync(double latitude = 40.99, double longitude = 29.02)
    {
        var job = NewJob(Guid.CreateVersion7(), latitude, longitude);
        await SaveAsync(db => db.DispatchJobs.Add(job));
        return job.OrderId;
    }

    /// <summary>A job in <c>Offering</c> (on its <paramref name="attempt"/>-th offer) with a courier who holds a pending offer.</summary>
    public async Task<SeededOffer> SeedPendingOfferAsync(int attempt = 1)
    {
        var now = Time.GetUtcNow();
        var orderId = Guid.CreateVersion7();
        var offerId = Guid.CreateVersion7();

        var job = NewJob(orderId, 40.99, 29.02);
        for (var earlier = 1; earlier < attempt; earlier++)
        {
            job.StartOffering(now).IsSuccess.ShouldBeTrue();
            job.OfferDeclined(Rules, now).IsSuccess.ShouldBeTrue();
        }

        job.StartOffering(now).IsSuccess.ShouldBeTrue();

        var courier = Courier.Register(Guid.CreateVersion7(), "Test Courier", now);
        courier.StartShift(now).IsSuccess.ShouldBeTrue();
        courier.ReceiveOffer(offerId, orderId, now).IsSuccess.ShouldBeTrue();

        var offer = Offer.Create(offerId, orderId, courier.Id, distanceMeters: 500, etaSeconds: 90, Rules, now);

        await SaveAsync(db =>
        {
            db.DispatchJobs.Add(job);
            db.Couriers.Add(courier);
            db.Offers.Add(offer);
        });
        return new SeededOffer(orderId, courier.Id, offerId, offer.ExpiresAt);
    }

    /// <summary>Like <see cref="SeedPendingOfferAsync"/>, but for a courier who already exists and is Available. Written with SQL only, so no push is ever sent.</summary>
    public async Task<SeededOffer> SeedPendingOfferForExistingCourierAsync(Guid courierId)
    {
        var now = Time.GetUtcNow();
        var orderId = Guid.CreateVersion7();
        var job = NewJob(orderId, 40.99, 29.02);
        job.StartOffering(now).IsSuccess.ShouldBeTrue();
        var offer = Offer.Create(Guid.CreateVersion7(), orderId, courierId, 500, 90, Rules, now);

        await SaveAsync(db =>
        {
            db.DispatchJobs.Add(job);
            db.Offers.Add(offer);
        });
        (await ExecuteAsync($"""
            UPDATE couriers SET state = 'Offered', current_offer_id = {offer.Id}, current_order_id = {orderId}, version = version + 1
            WHERE id = {courierId} AND state = 'Available'
            """)).ShouldBe(1);
        return new SeededOffer(orderId, courierId, offer.Id, offer.ExpiresAt);
    }

    /// <summary>A courier who accepted an offer: job <c>Completed</c>, courier <c>OnDelivery</c> on that order.</summary>
    public async Task<(Guid OrderId, Guid CourierId)> SeedOnDeliveryAsync()
    {
        var now = Time.GetUtcNow();
        var orderId = Guid.CreateVersion7();
        var offerId = Guid.CreateVersion7();

        var job = NewJob(orderId, 40.99, 29.02);
        job.StartOffering(now).IsSuccess.ShouldBeTrue();
        job.Complete(now).IsSuccess.ShouldBeTrue();

        var courier = Courier.Register(Guid.CreateVersion7(), "Test Courier", now);
        courier.StartShift(now).IsSuccess.ShouldBeTrue();
        courier.ReceiveOffer(offerId, orderId, now).IsSuccess.ShouldBeTrue();
        courier.AcceptOffer(offerId, now).IsSuccess.ShouldBeTrue();

        var offer = Offer.Create(offerId, orderId, courier.Id, 500, 90, Rules, now);
        offer.Accept(now).IsSuccess.ShouldBeTrue();

        await SaveAsync(db =>
        {
            db.DispatchJobs.Add(job);
            db.Couriers.Add(courier);
            db.Offers.Add(offer);
        });
        return (orderId, courier.Id);
    }

    private static DispatchJob NewJob(Guid orderId, double latitude, double longitude) =>
        DispatchJob.Start(orderId, $"merchant-{Guid.CreateVersion7():N}",
            new JobPlace(latitude, longitude, "Pickup Street 1"), new JobPlace(41.01, 29.05, "Dropoff Street 2"), TestClock.Start);

    private async Task SaveAsync(Action<DispatchDbContext> add)
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        add(db);
        await db.SaveChangesAsync();
    }

    // ---- Reading back --------------------------------------------------------------------------------------------------

    public Task<Courier> GetCourierAsync(Guid id) => QueryAsync(db => db.Couriers.AsNoTracking().SingleAsync(c => c.Id == id));

    public Task<DispatchJob> GetJobAsync(Guid orderId) => QueryAsync(db => db.DispatchJobs.AsNoTracking().SingleAsync(j => j.OrderId == orderId));

    public Task<Offer> GetOfferAsync(Guid id) => QueryAsync(db => db.Offers.AsNoTracking().SingleAsync(o => o.Id == id));

    public Task<List<Offer>> GetOffersOfOrderAsync(Guid orderId) =>
        QueryAsync(db => db.Offers.AsNoTracking().Where(o => o.OrderId == orderId).ToListAsync());

    public Task<List<OutboxMessage>> GetOutboxAsync(string? type = null) =>
        QueryAsync(db => db.OutboxMessages.AsNoTracking().Where(m => type == null || m.Type == type).OrderBy(m => m.OccurredAt).ToListAsync());

    /// <summary>The event's <c>data</c> object. Read as JSON, not as text: PostgreSQL's jsonb normalises spacing and key order.</summary>
    public static JsonElement DataOf(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonDocument.Parse(message.Payload).RootElement.GetProperty("data").Clone();
    }

    /// <summary>Runs a <c>SELECT count(*)::int AS "Value" …</c> and returns the number. For invariants that are easiest to state in SQL.</summary>
    public Task<int> CountAsync(FormattableString sql) => QueryAsync(db => db.Database.SqlQuery<int>(sql).SingleAsync());

    /// <summary>
    /// Delivers <c>OrderStatusChanged.v1</c> to Dispatch the way its consumer does: the handler runs inside a transaction that is
    /// committed afterwards (the inbox row and the broker are covered by the messaging tests of F03).
    /// </summary>
    public async Task OrderStatusChangedAsync(Guid orderId, string toStatus, CancellationToken cancellationToken)
    {
        var data = new OrderStatusChangedV1(orderId, "merchant-x", "ref-1", FromStatus: null, toStatus, CourierId: null, Reason: null, TestClock.Start, 5);
        var message = new EventEnvelope<OrderStatusChangedV1>(Guid.CreateVersion7(), OrderStatusChangedV1.EventType, TestClock.Start, orderId, 5, data);

        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<OrderStatusChangedHandler>().HandleAsync(message, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Runs a raw UPDATE/DELETE, to bring a row into a state the API alone cannot (a clock-independent shortcut).</summary>
    public Task<int> ExecuteAsync(FormattableString sql) => QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(sql));

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Tracking.DisposeAsync();
    }
}
