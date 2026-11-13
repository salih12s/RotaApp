using Microsoft.EntityFrameworkCore;
using Polly;
using StackExchange.Redis;
using Microsoft.Extensions.Http.Resilience;
using Rota.BuildingBlocks.Messaging;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Contracts.Tracking.V1;
using Rota.OpsApi.Features.Consumers;
using Rota.OpsApi.GraphQL;
using Rota.OpsApi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaAuth();
builder.AddRotaDbContext<OpsDbContext>(OpsDbContext.ConnectionName);
builder.AddRotaMessaging<OpsDbContext>();

// The read model is fed by three events (docs/05 §3). Each consumer has its own queue, so a slow one never holds up the others.
builder.AddRotaConsumer<OpsDbContext, OrderCreatedV1, OrderCreatedHandler>(
    queue: "q.ops.order-created", consumerName: "Ops.OrderCreatedConsumer");
builder.AddRotaConsumer<OpsDbContext, OrderStatusChangedV1, OrderStatusChangedHandler>(
    queue: "q.ops.order-status-changed", consumerName: "Ops.OrderStatusChangedConsumer");
builder.AddRotaConsumer<OpsDbContext, CourierStateChangedV1, CourierStateChangedHandler>(
    queue: "q.ops.courier-state-changed", consumerName: "Ops.CourierStateChangedConsumer");

// Resolvers and DataLoaders run in parallel, so each gets its own short-lived context from a factory (same options as the scoped one).
builder.Services.AddScoped<IDbContextFactory<OpsDbContext>>(sp => new OpsDbContextFactory(sp.GetRequiredService<DbContextOptions<OpsDbContext>>()));

builder.Services.AddOptions<OpsOptions>().BindConfiguration(OpsOptions.SectionName);
var ops = builder.Configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();

// Ops API -> Tracking: last locations for many couriers in one call (docs/05 §2).
builder.AddRotaServiceCredentials();
builder.Services.AddGrpcClient<CourierLocationQuery.CourierLocationQueryClient>((_, client) => client.Address = ops.TrackingAddress);
builder.Services.AddSingleton<TrackingBreaker>();
builder.Services.AddScoped<TrackingLocations>();

// Ops API -> Dispatch commands (docs/06 §6): 2 s timeout, circuit breaker, no retry. The dispatcher's own credentials are forwarded.
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CallerIdentityHandler>();
builder.Services.AddHttpClient<DispatchClient>(client => client.BaseAddress = ops.DispatchAddress)
    .AddHttpMessageHandler<CallerIdentityHandler>()
    .AddResilienceHandler("dispatch-commands", pipeline =>
    {
        // Outer to inner: the breaker sees the timeout as a failure; there is deliberately no retry (a second "assign" is not harmless).
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = ops.BreakerMinimumThroughput,
            BreakDuration = ops.BreakerDuration,
        });
        pipeline.AddTimeout(ops.DispatchTimeout);
    });

// Tracking links: the public key comes from Orders; this service can verify tokens but never mint them (ADR-007).
// A singleton: it keeps the fetched keys, so Orders is asked once per cache lifetime, not once per subscription.
builder.Services.AddHttpClient(TrackingTokenValidator.HttpClientName);
builder.Services.AddSingleton<TrackingTokenValidator>();
builder.Services.AddSingleton<OrderStatusNotifier>();

// Redis: Tracking's position channels, and the bus that carries subscription events between Ops API instances (docs/05 §5).
// Not AbortOnConnectFail: the service starts without Redis; only live subscriptions suffer until it is back.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
    var options = ConfigurationOptions.Parse(connection);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<CourierLocationFeed>();

builder.Services.AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddSubscriptionType<Subscription>()
    .AddMutationConventions(applyToAllMutations: true)
    .AddErrorFilter<OpsErrorFilter>()
    .AddRedisSubscriptions(sp => sp.GetRequiredService<IConnectionMultiplexer>())
    .AddType<OrderType>()
    .AddType<CourierType>()
    .AddDataLoader<CourierByIdLoader>()
    .AddDataLoader<OrderByIdLoader>()
    .AddDataLoader<LastLocationLoader>()
    .AddFiltering()
    .AddSorting()
    // A page of 50 orders with courier and position costs ~1000 by Hot Chocolate's count (a list field multiplies its children by the page size),
    // so the default limit of 1000 would refuse the panel's own main screen. 2500 fits it with room; a page of 200 with nesting does not.
    .ModifyCostOptions(options =>
    {
        options.MaxFieldCost = 2_500;
        options.MaxTypeCost = 2_500;
    })
    // The panel's deepest legitimate query is orders > courier > currentOrder > courier > lastLocation (6 levels with the connection wrapper); 10 leaves room.
    .AddMaxExecutionDepthRule(10)
    .ModifyRequestOptions(options => options.IncludeExceptionDetails = builder.Environment.IsDevelopment());
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWebSockets();
app.MapGraphQL();

app.Run();

/// <summary>Visible to the test projects (WebApplicationFactory).</summary>
public partial class Program;
