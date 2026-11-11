using Google.Protobuf.WellKnownTypes;
using Rota.Contracts.Tracking.V1;
using Rota.TestKit;

namespace Rota.Tracking.IntegrationTests.Support;

/// <summary>Helpers to play the courier's side of the position stream.</summary>
public static class Streams
{
    public static async Task<StreamLocationsResult> SendAsync(TrackingApiFactory api, Guid courier, CancellationToken cancellationToken, params LocationPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(api);

        using var channel = api.CreateGrpcChannel();
        var client = new CourierLocationIngest.CourierLocationIngestClient(channel);
        using var call = client.StreamLocations(TrackingApiFactory.As(DevUsers.Courier(courier)), cancellationToken: cancellationToken);
        foreach (var point in points)
        {
            await call.RequestStream.WriteAsync(point, cancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    /// <param name="seq">Sequence number on the courier's device.</param>
    /// <param name="lat">Latitude.</param>
    /// <param name="lon">Longitude.</param>
    /// <param name="accuracy">GPS accuracy in metres; 0 means "not reported".</param>
    /// <param name="recordedAt">Device time; defaults to the test clock's start, which is also the service's "now".</param>
    public static LocationPoint Point(long seq, double lat = 40.99, double lon = 29.02, float accuracy = 5, DateTimeOffset? recordedAt = null) =>
        new()
        {
            Seq = seq,
            Lat = lat,
            Lon = lon,
            AccuracyM = accuracy,
            RecordedAt = Timestamp.FromDateTimeOffset(recordedAt ?? TestClock.Start),
        };
}
