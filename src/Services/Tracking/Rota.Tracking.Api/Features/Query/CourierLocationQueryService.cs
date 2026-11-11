using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Tracking.V1;
using Rota.Tracking.Api.Domain;
using Rota.Tracking.Api.Infrastructure;

namespace Rota.Tracking.Api.Features.Query;

/// <summary>
/// Internal reads for Dispatch ("who is near this pickup?") and the Ops API ("where are these couriers?").
/// Only other services may call: a courier asking where the other couriers are would be a leak.
/// </summary>
[Authorize(Policy = RotaPolicies.ServiceOnly)]
public sealed class CourierLocationQueryService(CourierQueries queries, TimeProvider time) : CourierLocationQuery.CourierLocationQueryBase
{
    public override async Task<GetNearbyCouriersResponse> GetNearbyCouriers(GetNearbyCouriersRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = QueryRules.ValidateNearby(request.Lat, request.Lon, request.RadiusM, request.Limit, request.MaxAgeSeconds);
        if (query.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, query.Error.Message));
        }

        var found = await queries.NearbyAsync(query.Value, time.GetUtcNow());

        var response = new GetNearbyCouriersResponse();
        response.Couriers.AddRange(found.Select(c => new NearbyCourier
        {
            CourierId = c.CourierId.ToString("D"),
            DistanceM = (int)Math.Round(c.DistanceMeters),
            LastSeenAt = Timestamp.FromDateTimeOffset(c.LastSeenAt),
        }));
        return response;
    }

    public override async Task<GetLastLocationsResponse> GetLastLocations(GetLastLocationsRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ids = QueryRules.ValidateCourierIds(request.CourierIds);
        if (ids.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ids.Error.Message));
        }

        var found = await queries.LastLocationsAsync(ids.Value);

        var response = new GetLastLocationsResponse();
        response.Locations.AddRange(found.Select(l => new CourierLocation
        {
            CourierId = l.CourierId.ToString("D"),
            Lat = l.Lat,
            Lon = l.Lon,
            RecordedAt = Timestamp.FromDateTimeOffset(l.RecordedAt),
        }));
        return response;
    }
}
