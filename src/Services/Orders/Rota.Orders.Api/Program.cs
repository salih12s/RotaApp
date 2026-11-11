using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Events.Dispatch;
using Rota.Orders.Api.Domain;
using Rota.Orders.Api.Features;
using Rota.Orders.Api.Features.Consumers;
using Rota.Orders.Api.Features.CreateOrder;
using Rota.Orders.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaDevAuth();
builder.AddRotaDbContext<OrdersDbContext>(OrdersDbContext.ConnectionName);
builder.AddRotaMessaging<OrdersDbContext>();
builder.AddRotaConsumer<OrdersDbContext, DispatchStartedV1, DispatchStartedHandler>(
    queue: "q.orders.dispatch-started", consumerName: "Orders.DispatchStartedConsumer");
builder.Services.AddOpenApi();
builder.Services.AddValidation();

// Fail at startup, not on the first order, when a key is missing or malformed.
builder.Services.AddOptions<OrdersOptions>()
    .BindConfiguration(OrdersOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(OrdersOptions.HasValidKeys, "Orders keys must be base64: DeliveryCodeHashKey >= 32 bytes, ResponseEncryptionKey = 32 bytes.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
    new DeliveryCodeHasher(OrdersOptions.Decode(sp.GetRequiredService<IOptions<OrdersOptions>>().Value.DeliveryCodeHashKey)));
builder.Services.AddSingleton(sp =>
    new SecretProtector(OrdersOptions.Decode(sp.GetRequiredService<IOptions<OrdersOptions>>().Value.ResponseEncryptionKey)));
builder.Services.AddSingleton<TrackingTokenIssuer>();
builder.Services.AddSingleton(ServiceAreaPolicy.Default);
builder.Services.AddScoped<CreateOrderHandler>();

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapOrdersEndpoints();

app.Run();
