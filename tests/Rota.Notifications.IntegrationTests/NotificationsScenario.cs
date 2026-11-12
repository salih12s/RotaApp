using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Features.Consumers;
using Rota.Notifications.Api.Features.Delivery;
using Rota.Notifications.Api.Infrastructure;
using Rota.Notifications.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Notifications.IntegrationTests;

/// <summary>The real Notifications API in memory, on a test database, with a configurable clock and key directory. Environment stays Development (DevAuth).</summary>
public sealed class NotificationsApiFactory(
    string connectionString, string keyDirectory, TimeProvider time, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Notifications", connectionString);
        builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("Notifications:KeyDirectory", keyDirectory);

        // Tests drive the delivery rounds by hand unless one says otherwise; the receivers run on this machine, hence http and loopback.
        builder.UseSetting("Notifications:WorkerEnabled", "false");
        builder.UseSetting("Notifications:AllowHttp", "true");
        builder.UseSetting("Notifications:AllowPrivateNetworks", "true");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton(time));
    }
}

/// <summary>Everything one test needs: its own database, a fake merchant, the service on a clock, and helpers to feed it events and read the result.</summary>
public sealed class NotificationsScenario : IAsyncDisposable
{
    private NotificationsScenario(FakeMerchantServer merchant, NotificationsApiFactory api, TimeProvider time, string connection, string keyDirectory)
    {
        Merchant = merchant;
        Api = api;
        Time = time;
        Connection = connection;
        KeyDirectory = keyDirectory;
    }

    public FakeMerchantServer Merchant { get; }

    public NotificationsApiFactory Api { get; }

    public TimeProvider Time { get; }

    public FakeTimeProvider Fake => (FakeTimeProvider)Time;

    public string Connection { get; }

    public string KeyDirectory { get; }

    public static async Task<NotificationsScenario> StartAsync(
        NotificationsFixture fixture, IReadOnlyDictionary<string, string>? settings = null, TimeProvider? time = null,
        string? connection = null, string? keyDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var clock = time ?? new FakeTimeProvider(TestClock.Start);
        var merchant = await FakeMerchantServer.StartAsync(clock);
        var database = connection ?? await fixture.CreateDatabaseAsync(cancellationToken);
        var keys = keyDirectory ?? Path.Combine(Path.GetTempPath(), $"rota-keys-{Guid.NewGuid():N}");
        var api = new NotificationsApiFactory(database, keys, clock, settings);
        _ = api.Services; // builds and starts the host
        return new NotificationsScenario(merchant, api, clock, database, keys);
    }

    public HttpClient ClientAs(string devUser) => Api.CreateClient().As(devUser);

    public AsyncServiceScope Scope() => Api.Services.CreateAsyncScope();

    public async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Scope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    /// <summary>A merchant subscribes (PUT) to <paramref name="path"/> on the fake server; returns the secret and tells the fake server about it.</summary>
    public async Task<string> SubscribeAsync(string merchantId, string path)
    {
        using var client = ClientAs(DevUsers.Merchant(merchantId));
        using var response = await client.PutAsJsonAsync("/api/v1/webhook-subscription", new { url = Merchant.UrlFor(path) });
        response.EnsureSuccessStatusCode();
        var secret = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        Merchant.SetSecret(path, secret);
        return secret;
    }

    /// <summary>Delivers <c>OrderStatusChanged.v1</c> to the consumer's handler the way the consumer does: in a transaction, committed afterwards.</summary>
    public async Task<Guid> OrderStatusChangedAsync(
        string merchantId, string toStatus, int orderVersion = 2, Guid? orderId = null, Guid? courierId = null, Guid? eventId = null)
    {
        var id = eventId ?? Guid.CreateVersion7();
        var order = orderId ?? Guid.CreateVersion7();
        var data = new OrderStatusChangedV1(order, merchantId, "ref-1", FromStatus: null, toStatus, courierId, Reason: null, TestClock.Start, orderVersion);
        var envelope = new EventEnvelope<OrderStatusChangedV1>(id, OrderStatusChangedV1.EventType, TestClock.Start, order, orderVersion, data);

        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new OrderStatusChangedHandler(db, Time).HandleAsync(envelope, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return id;
    }

    public async Task CourierStateChangedAsync(Guid courierId, string displayName, int version)
    {
        var data = new CourierStateChangedV1(courierId, displayName, "Available", CurrentOrderId: null, TestClock.Start, version);
        var envelope = new EventEnvelope<CourierStateChangedV1>(Guid.CreateVersion7(), CourierStateChangedV1.EventType, TestClock.Start, courierId, version, data);

        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new CourierStateChangedHandler(db).HandleAsync(envelope, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public Task<int> RunRoundAsync(CancellationToken cancellationToken = default) =>
        Api.Services.GetRequiredService<DeliveryRounds>().RunOnceAsync(cancellationToken);

    /// <summary>Moves the fake clock forward and runs one round, <paramref name="times"/> times: "time passes, the worker wakes up".</summary>
    public async Task AdvanceAndRunAsync(TimeSpan step, int times, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < times; i++)
        {
            Fake.Advance(step);
            await RunRoundAsync(cancellationToken);
        }
    }

    public Task<List<WebhookDelivery>> DeliveriesAsync() =>
        QueryAsync(db => db.Deliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    public Task<WebhookDelivery> DeliveryAsync(Guid id) => QueryAsync(db => db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id));

    public Task<int> CountAsync(DeliveryStatus status) => QueryAsync(db => db.Deliveries.CountAsync(d => d.Status == status));

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        await Merchant.DisposeAsync();
    }
}
