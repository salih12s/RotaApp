using Grpc.Core;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Tracking.V1;

namespace Rota.OpsApi.Infrastructure;

/// <summary>The newest position Tracking has for a courier.</summary>
public sealed record CourierLocationInfo(double Lat, double Lon, DateTimeOffset RecordedAt);

/// <summary>
/// The circuit breaker for Tracking calls. One instance for the whole process: a breaker that is rebuilt per request never sees enough failures to open.
/// </summary>
public sealed class TrackingBreaker(IOptions<OpsOptions> options)
{
    public ResiliencePipeline Pipeline { get; } = new ResiliencePipelineBuilder()
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<RpcException>(TrackingLocations.IsOutage),
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = options.Value.BreakerMinimumThroughput,
            BreakDuration = options.Value.BreakerDuration,
        })
        .Build();
}

/// <summary>
/// Ops API → Tracking <c>GetLastLocations</c> (docs/05 §2, docs/06 §6): one call for many couriers (the DataLoader batches them), authenticated as
/// a service, 300 ms deadline, circuit breaker, no retry. Tracking does not answer for a courier it has never heard of; such a courier is simply
/// absent from the result. If Tracking is slow or down the answer is <b>degraded</b>: no positions, but the orders and couriers still come back.
/// </summary>
public sealed partial class TrackingLocations(
    CourierLocationQuery.CourierLocationQueryClient client,
    ServiceCredentials credentials,
    TrackingBreaker breaker,
    IOptions<OpsOptions> options,
    ILogger<TrackingLocations> logger)
{
    private static readonly IReadOnlyDictionary<Guid, CourierLocationInfo> _none = new Dictionary<Guid, CourierLocationInfo>();

    internal static bool IsOutage(RpcException ex) => ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Unavailable;

    public async Task<IReadOnlyDictionary<Guid, CourierLocationInfo>> GetLastAsync(IReadOnlyList<Guid> courierIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(courierIds);

        try
        {
            return await breaker.Pipeline.ExecuteAsync(async token => await CallAsync(courierIds, token), cancellationToken);
        }
        catch (Exception ex) when (ex is RpcException { StatusCode: StatusCode.DeadlineExceeded or StatusCode.Unavailable } or BrokenCircuitException or HttpRequestException)
        {
            LogDegraded(logger, ex.GetType().Name);
            return _none;
        }
    }

    private async ValueTask<IReadOnlyDictionary<Guid, CourierLocationInfo>> CallAsync(IReadOnlyList<Guid> courierIds, CancellationToken cancellationToken)
    {
        var request = new GetLastLocationsRequest();
        request.CourierIds.AddRange(courierIds.Select(id => id.ToString("D")));

        var token = await credentials.GetBearerAsync(cancellationToken);
        var caller = token is null ? new Metadata { { "x-dev-user", "service:ops-api" } } : new Metadata { { "authorization", $"Bearer {token}" } };

        // A gRPC deadline is an absolute instant on the real clock (see Dispatch's TrackingCourierLocator).
        var response = await client.GetLastLocationsAsync(request, caller, DateTime.UtcNow + options.Value.TrackingDeadline, cancellationToken);

        return response.Locations.ToDictionary(
            l => Guid.Parse(l.CourierId),
            l => new CourierLocationInfo(l.Lat, l.Lon, l.RecordedAt.ToDateTimeOffset()));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tracking could not answer GetLastLocations ({Reason}); the response goes out without positions")]
    private static partial void LogDegraded(ILogger logger, string reason);
}
