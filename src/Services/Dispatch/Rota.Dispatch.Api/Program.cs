using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.Contracts.Events.Orders;
using Rota.Dispatch.Api.Features.Consumers;
using Rota.Dispatch.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaDbContext<DispatchDbContext>(DispatchDbContext.ConnectionName);
builder.AddRotaMessaging<DispatchDbContext>();
builder.AddRotaConsumer<DispatchDbContext, OrderCreatedV1, OrderCreatedHandler>(
    queue: "q.dispatch.order-created", consumerName: "Dispatch.OrderCreatedConsumer");
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRotaServiceDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();
