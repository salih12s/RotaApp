using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Rota.BuildingBlocks.Web.Errors;

namespace Rota.BuildingBlocks.Web;

/// <summary>
/// Shared infrastructure every Rota service registers: ProblemDetails, health checks, OpenTelemetry.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    private const string LiveTag = "live";
    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Same string as Rota.BuildingBlocks.Messaging.MessagingTelemetry.Name (Web does not reference Messaging).
    private const string MessagingSourceName = "Rota.Messaging";

    // Same string as Rota.Tracking.Api's TrackingTelemetry.MeterName.
    private const string TrackingMeterName = "Rota.Tracking";

    // Same string as Rota.Dispatch.Api's DispatchTelemetry.MeterName.
    private const string DispatchMeterName = "Rota.Dispatch";

    public static TBuilder AddRotaServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;

            // Framework-made problems (401 challenge, validation 400, unknown route 404) get a
            // catalog type too, so clients switch on one vocabulary.
            if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/", StringComparison.Ordinal))
            {
                var code = ErrorCatalog.CodeForStatus(problem.Status ?? context.HttpContext.Response.StatusCode);
                problem.Type = ErrorCatalog.TypeUri(code);
                problem.Title ??= ErrorCatalog.Lookup(code).Title;
            }

            problem.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
        });
        builder.Services.AddExceptionHandler<UnexpectedExceptionHandler>();

        // Business code reads time only through TimeProvider; tests swap in FakeTimeProvider.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Liveness: the process answers. Readiness checks (DB, broker) are added by each service later.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        builder.AddRotaOpenTelemetry();

        return builder;
    }

    /// <summary>Call right after <c>Build()</c>: exception handling must be first in the pipeline.</summary>
    public static WebApplication UseRotaServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });
        app.MapHealthChecks(ReadyPath);

        return app;
    }

    private static void AddRotaOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
            .WithMetrics(metrics => metrics
                .AddMeter(MessagingSourceName)
                .AddMeter(TrackingMeterName)
                .AddMeter(DispatchMeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(MessagingSourceName)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments(new PathString("/health"), StringComparison.Ordinal))
                .AddHttpClientInstrumentation());

        // Export only when an endpoint is configured (launchSettings / compose), so tests stay silent.
        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            otel.UseOtlpExporter();
        }
    }
}
