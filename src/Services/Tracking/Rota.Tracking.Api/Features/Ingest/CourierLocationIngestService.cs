using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Tracking.V1;
using Rota.Tracking.Api.Domain;
using Rota.Tracking.Api.Infrastructure;

namespace Rota.Tracking.Api.Features.Ingest;

/// <summary>
/// The courier's position stream: one long-lived call, a point every few seconds. Client streaming
/// means the courier connects once and keeps sending, instead of opening a new request per point.
/// </summary>
[Authorize(Policy = RotaPolicies.CourierOnly)]
public sealed class CourierLocationIngestService(
    CurrentPositionStore positions, LocationHistoryWriter history, TimeProvider time, IOptions<TrackingOptions> options)
    : CourierLocationIngest.CourierLocationIngestBase
{
    public override async Task<StreamLocationsResult> StreamLocations(
        IAsyncStreamReader<LocationPoint> requestStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(context);

        var settings = options.Value;

        // Who is sending comes from the authenticated principal, never from the messages: otherwise a
        // courier could report positions on behalf of another one (a FAIL signal of this phase).
        var courierId = context.GetHttpContext().User.GetCourierId()
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "A courier identity is required."));

        long received = 0, accepted = 0, notNewer = 0;
        await foreach (var point in requestStream.ReadAllAsync(context.CancellationToken))
        {
            received++;
            var now = time.GetUtcNow();
            var recordedAt = point.RecordedAt?.ToDateTimeOffset();

            var valid = LocationRules.Validate(
                point.Seq, point.Lat, point.Lon, point.AccuracyM, recordedAt, now, settings.MaxPointAge, settings.MaxClockSkew);
            if (valid.IsFailure)
            {
                // A client bug, not something retrying fixes: end the call. Points already handled stay.
                throw new RpcException(new Status(StatusCode.InvalidArgument, valid.Error.Message));
            }

            // 1) The current position: Redis decides whether this point is newer than what it holds.
            var position = new CourierPosition(courierId, point.Seq, point.Lat, point.Lon, recordedAt!.Value);
            if (await positions.TryUpdateAsync(position, now))
            {
                accepted++;
            }
            else
            {
                notNewer++;
            }

            // 2) The history: every valid point, newer or not. An old point is no longer the current position
            // but it did happen, and the route history should show it. Waits when the queue is full.
            // (0 is protobuf's "not set": no real GPS reports an accuracy of exactly zero metres.)
            await history.EnqueueAsync(
                new LocationRecord(courierId, point.Seq, point.Lat, point.Lon, point.AccuracyM > 0 ? point.AccuracyM : null, recordedAt.Value, now),
                context.CancellationToken);
        }

        return new StreamLocationsResult { Received = received, Accepted = accepted, OutOfOrder = notNewer };
    }
}
