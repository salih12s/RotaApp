using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Notifications.Api.Domain;
using Rota.Notifications.Api.Features;
using Rota.Notifications.Api.Features.Consumers;
using Rota.Notifications.Api.Features.Delivery;
using Rota.Notifications.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaAuth();
builder.AddRotaDbContext<NotificationsDbContext>(NotificationsDbContext.ConnectionName);
builder.AddRotaMessaging<NotificationsDbContext>();
builder.AddRotaConsumer<NotificationsDbContext, OrderStatusChangedV1, OrderStatusChangedHandler>(
    queue: "q.notifications.order-status-changed", consumerName: "Notifications.OrderStatusChangedConsumer");
builder.AddRotaConsumer<NotificationsDbContext, CourierStateChangedV1, CourierStateChangedHandler>(
    queue: "q.notifications.courier-state-changed", consumerName: "Notifications.CourierStateChangedConsumer");
builder.Services.AddOpenApi();
builder.Services.AddValidation();

builder.Services.AddOptions<NotificationsOptions>()
    .BindConfiguration(NotificationsOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.KeyDirectory), "Notifications:KeyDirectory is required: the keys must outlive a restart.")
    .Validate(o => o.DeliveryTimeout > TimeSpan.Zero && o.Lease > o.DeliveryTimeout, "Notifications:Lease must be longer than DeliveryTimeout, or a slow delivery would be sent twice.")
    .Validate(o => o.MerchantConcurrency > 0 && o.PerMerchantClaim > 0 && o.WorkerBatchSize > 0 && o.CircuitThreshold > 0, "Notifications worker limits must be positive.")
    .ValidateOnStart();

// The keys that encrypt webhook secrets live on disk (a shared volume in containers). Without a persisted key ring every restart would
// create new keys and every stored secret would become unreadable.
builder.Services.AddDataProtection()
    .SetApplicationName("Rota.Notifications")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration.GetSection(NotificationsOptions.SectionName)[nameof(NotificationsOptions.KeyDirectory)] ?? "data-protection-keys"));

builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<WebhookUrlValidator>();
builder.Services.AddSingleton<MerchantGates>();
builder.Services.AddSingleton(BackoffSchedule.Default);
builder.Services.AddHttpClient<WebhookSender>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(sp => WebhookSender.CreateHandler(sp.GetRequiredService<IOptions<NotificationsOptions>>().Value.AllowPrivateNetworks));
builder.Services.AddSingleton<DeliveryRounds>();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWebhookEndpoints();

app.Run();
