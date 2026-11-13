using Grpc.Core;
using Grpc.Net.Client.Configuration;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Events.Orders;
using Rota.Contracts.Tracking.V1;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features;
using Rota.Dispatch.Api.Features.Consumers;
using Rota.Dispatch.Api.Features.Jobs;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Features.Offers;
using Rota.Dispatch.Api.Features.Shifts;
using Rota.Dispatch.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaAuth();
builder.AddRotaServiceCredentials();

// Runs the actions queued with DbContext.AfterCommit (e.g. the Redis "new offer" push) once a transaction has committed.
builder.Services.AddSingleton<IInterceptor, AfterCommitInterceptor>();
builder.AddRotaDbContext<DispatchDbContext>(DispatchDbContext.ConnectionName);
builder.AddDispatchRedis();
builder.AddRotaMessaging<DispatchDbContext>();
builder.AddRotaConsumer<DispatchDbContext, OrderCreatedV1, OrderCreatedHandler>(
    queue: "q.dispatch.order-created", consumerName: "Dispatch.OrderCreatedConsumer");
builder.AddRotaConsumer<DispatchDbContext, OrderStatusChangedV1, OrderStatusChangedHandler>(
    queue: "q.dispatch.order-status-changed", consumerName: "Dispatch.OrderStatusChangedConsumer");
builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddValidation();

builder.Services.AddOptions<DispatchOptions>()
    .BindConfiguration(DispatchOptions.SectionName)
    .Validate(o => o.Rules.OfferTtl > TimeSpan.Zero && o.Rules.MaxOfferAttempts > 0, "Dispatch:Rules must be positive.")
    .Validate(o => o.WorkerPollInterval > TimeSpan.Zero && o.WorkerBatchSize > 0 && o.SearchLease > TimeSpan.Zero && o.StreamHeartbeat > TimeSpan.Zero,
        "Dispatch worker and stream timings must be positive.")
    .Validate(o => o.NearbyDeadline > TimeSpan.Zero && o.SearchRadiusMeters is >= 1 and <= 50_000 && o.CandidateLimit is >= 1 and <= 100,
        "Dispatch:NearbyDeadline, SearchRadiusMeters (1-50000) and CandidateLimit (1-100) must be in the range Tracking accepts.")
    .ValidateOnStart();

// Dispatch → Tracking (docs/05 §2, docs/06 §6): the 200 ms deadline is set per call; the channel retries once, only on UNAVAILABLE.
builder.Services.AddGrpcClient<CourierLocationQuery.CourierLocationQueryClient>((services, client) =>
        client.Address = new Uri(services.GetRequiredService<IOptions<DispatchOptions>>().Value.TrackingAddress))
    .ConfigureChannel(channel => channel.ServiceConfig = new ServiceConfig
    {
        MethodConfigs =
        {
            new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = new RetryPolicy
                {
                    MaxAttempts = 2,
                    InitialBackoff = TimeSpan.FromMilliseconds(20),
                    MaxBackoff = TimeSpan.FromMilliseconds(50),
                    BackoffMultiplier = 1.5,
                    RetryableStatusCodes = { StatusCode.Unavailable },
                },
            },
        },
    });

builder.Services.AddSingleton(new EtaCalculator());
builder.Services.AddSingleton<OfferNotifier>();
builder.Services.AddScoped<TrackingCourierLocator>();
builder.Services.AddScoped<OfferingService>();
builder.Services.AddScoped<OfferResponseService>();
builder.Services.AddScoped<OfferTimeoutProcessor>();
builder.Services.AddScoped<ShiftService>();
builder.Services.AddScoped<JobCommandService>();
builder.Services.AddSingleton<DispatchRounds>();
builder.Services.AddHostedService<DispatchWorker>();

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDispatchEndpoints();
app.MapGrpcService<CourierOffersService>();

app.Run();
