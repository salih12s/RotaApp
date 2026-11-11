using Npgsql;
using Rota.BuildingBlocks.Persistence;
using Rota.BuildingBlocks.Web;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Tracking.Api.Features.Ingest;
using Rota.Tracking.Api.Features.Query;
using Rota.Tracking.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddRotaServiceDefaults();
builder.AddRotaDevAuth();
builder.AddRotaDbContext<TrackingDbContext>(TrackingDbContext.ConnectionName);
builder.AddTrackingRedis();
builder.Services.AddGrpc();
builder.Services.AddOpenApi();

builder.Services.AddOptions<TrackingOptions>()
    .BindConfiguration(TrackingOptions.SectionName)
    .Validate(o => o.HistoryQueueCapacity > 0 && o.HistoryBatchSize > 0 && o.HistoryMaxWait > TimeSpan.Zero, "Tracking history queue settings must be positive.")
    .Validate(o => o.MaxPointAge <= TimeSpan.FromDays(1), "Tracking:MaxPointAge may not exceed 24 h: the maintenance job keeps partitions from yesterday on.")
    .ValidateOnStart();

// The hot path talks to PostgreSQL directly (COPY), not through EF.
builder.Services.AddSingleton(sp =>
    NpgsqlDataSource.Create(sp.GetRequiredService<IConfiguration>().GetConnectionString(TrackingDbContext.ConnectionName)
        ?? throw new InvalidOperationException($"Connection string '{TrackingDbContext.ConnectionName}' is not configured.")));

builder.Services.AddSingleton<CurrentPositionStore>();
builder.Services.AddSingleton<CourierQueries>();
builder.Services.AddSingleton<PartitionMaintainer>();

// Order matters: partitions are made before the history writer starts and before traffic arrives.
builder.Services.AddHostedService<PartitionMaintenanceService>();
builder.Services.AddSingleton<LocationHistoryWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocationHistoryWriter>());

var app = builder.Build();

app.UseRotaServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGrpcService<CourierLocationIngestService>();
app.MapGrpcService<CourierLocationQueryService>();

app.Run();
