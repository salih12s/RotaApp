extern alias DispatchApi;
extern alias NotificationsApi;
extern alias OrdersApi;
extern alias TrackingApi;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OrdersApi::Rota.Orders.Api.Infrastructure;
using TrackingApi::Rota.Tracking.Api.Infrastructure;
using Rota.Auth.IntegrationTests;
using Rota.BuildingBlocks.Persistence;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(AuthFixture))]

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// One Keycloak (with the real seed realm), one PostgreSQL and one Redis for the whole assembly, and the services started the way
/// production starts them: environment <c>Production</c>, so DevAuth is off and only real JWTs count.
/// </summary>
public sealed class AuthFixture : IAsyncLifetime
{
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.8.2")
        .WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, "realm-rota.json")), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.10.3-alpine").Build();
    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("rota-auth-keys-").FullName;

    public string Authority { get; private set; } = string.Empty;

    public string RedisConnection => _redis.GetConnectionString();

    public KeycloakTokens Tokens { get; private set; } = null!;

    public ServiceHost<OrdersApi::Program> Orders { get; private set; } = null!;

    public ServiceHost<DispatchApi::Program> Dispatch { get; private set; } = null!;

    public ServiceHost<NotificationsApi::Program> Notifications { get; private set; } = null!;

    public ServiceHost<TrackingApi::Program> Tracking { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_keycloak.StartAsync(), _postgres.StartAsync(), _redis.StartAsync());

        Authority = $"{_keycloak.GetBaseAddress().ToString().TrimEnd('/')}/realms/rota";
        Tokens = new KeycloakTokens(Authority);

        var ordersConnection = await CreateDatabaseAsync("rota_orders");
        var options = new DbContextOptionsBuilder<OrdersDbContext>();
        options.UseRotaNpgsql(ordersConnection);
        await using (var db = new OrdersDbContext(options.Options))
        {
            await db.Database.MigrateAsync();
        }

        var trackingConnection = await CreateDatabaseAsync("rota_tracking");
        var trackingOptions = new DbContextOptionsBuilder<TrackingDbContext>();
        trackingOptions.UseRotaNpgsql(trackingConnection);
        await using (var db = new TrackingDbContext(trackingOptions.Options))
        {
            await db.Database.MigrateAsync();
        }

        // Tracking talks gRPC only; its start-up also needs the migrated database (partition maintenance runs at start).
        Tracking = new ServiceHost<TrackingApi::Program>(ProductionSettings(new Dictionary<string, string>
        {
            ["ConnectionStrings:Tracking"] = trackingConnection,
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
        }));

        Orders = new ServiceHost<OrdersApi::Program>(ProductionSettings(new Dictionary<string, string>
        {
            ["ConnectionStrings:Orders"] = ordersConnection,
            ["Messaging:Enabled"] = "false",
            ["Orders:DeliveryCodeHashKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Orders:ResponseEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Orders:TrackingSigningKey"] = SigningKeyPem(),
        }));

        // Dispatch and Notifications answer 401/403 before they touch a database, so these two never connect to anything.
        Dispatch = new ServiceHost<DispatchApi::Program>(ProductionSettings(new Dictionary<string, string>
        {
            ["ConnectionStrings:Dispatch"] = await CreateDatabaseAsync("rota_dispatch"),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Dispatch:TrackingAddress"] = "http://localhost:1",
            ["Dispatch:WorkersEnabled"] = "false",
            ["Messaging:Enabled"] = "false",
        }));

        Notifications = new ServiceHost<NotificationsApi::Program>(ProductionSettings(new Dictionary<string, string>
        {
            ["ConnectionStrings:Notifications"] = await CreateDatabaseAsync("rota_notifications"),
            ["Notifications:KeyDirectory"] = _keyDirectory,
            ["Notifications:WorkerEnabled"] = "false",
            ["Messaging:Enabled"] = "false",
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await Orders.DisposeAsync();
        await Dispatch.DisposeAsync();
        await Notifications.DisposeAsync();
        await Tracking.DisposeAsync();
        Tokens.Dispose();
        await Task.WhenAll(_keycloak.DisposeAsync().AsTask(), _postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        Directory.Delete(_keyDirectory, recursive: true);
    }

    /// <summary>Settings of a service started like production: <c>Production</c> environment, Keycloak as the only way in.</summary>
    public Dictionary<string, string> ProductionSettings(Dictionary<string, string> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        own["environment"] = "Production";
        own["Auth:Authority"] = Authority;
        own["Auth:RequireHttpsMetadata"] = "false"; // the test Keycloak speaks plain http
        return own;
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private static string SigningKeyPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
