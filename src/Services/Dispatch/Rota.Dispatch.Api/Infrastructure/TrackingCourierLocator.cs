using Grpc.Core;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Tracking.V1;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>A courier Tracking saw recently near a point (already filtered for freshness by Tracking).</summary>
public sealed record NearbyCourierInfo(Guid CourierId, int DistanceMeters, DateTimeOffset LastSeenAt);

/// <summary>
/// Dispatch → Tracking <c>GetNearbyCouriers</c> (docs/05 §2, docs/06 §6): 200 ms deadline for the whole call,
/// one retry only on <c>UNAVAILABLE</c> (configured on the channel, see Program.cs). A failure comes back as a
/// result, not an exception: "Tracking is slow" is an expected situation, and the job just waits and tries again.
/// </summary>
public sealed partial class TrackingCourierLocator(
    CourierLocationQuery.CourierLocationQueryClient client,
    IOptions<DispatchOptions> options,
    ServiceCredentials credentials,
    ILogger<TrackingCourierLocator> logger)
{
    public static readonly Error Unavailable = new("dependency-unavailable", "Tracking could not answer in time.");


    public async Task<Result<IReadOnlyList<NearbyCourierInfo>>> FindNearbyAsync(
        double latitude, double longitude, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var request = new GetNearbyCouriersRequest
        {
            Lat = latitude,
            Lon = longitude,
            RadiusM = settings.SearchRadiusMeters,
            Limit = settings.CandidateLimit,
        };

        try
        {
            // A gRPC deadline is an absolute wall-clock instant sent to the server, so it has to be the real
            // clock; the business TimeProvider (frozen in tests) would give an instant in the past.
            var deadline = DateTime.UtcNow + settings.NearbyDeadline;
            var token = await credentials.GetBearerAsync(cancellationToken);
            var caller = token is null ? new Metadata { { "x-dev-user", "service:dispatch" } } : new Metadata { { "authorization", $"Bearer {token}" } };
            var response = await client.GetNearbyCouriersAsync(request, caller, deadline, cancellationToken);

            IReadOnlyList<NearbyCourierInfo> couriers = [.. response.Couriers.Select(c => new NearbyCourierInfo(
                Guid.Parse(c.CourierId), c.DistanceM, c.LastSeenAt.ToDateTimeOffset()))];
            return Result.Success(couriers);
        }
        catch (Exception ex) when (ex is RpcException { StatusCode: StatusCode.DeadlineExceeded or StatusCode.Unavailable } or HttpRequestException)
        {
            LogTrackingFailed(logger, (ex as RpcException)?.StatusCode ?? StatusCode.Unavailable);
            return Unavailable;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tracking GetNearbyCouriers failed with {StatusCode}; the search will be retried later")]
    private static partial void LogTrackingFailed(ILogger logger, StatusCode statusCode);
}
