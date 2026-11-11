using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Rota.Contracts.Tracking.V1;
using Rota.Tracking.Api.Infrastructure;
using Rota.TestKit;
using StackExchange.Redis;

namespace Rota.Tracking.IntegrationTests.Ingest;

/// <summary>
/// The current position in Redis: R-8 (out of order), duplicates, publishing and the race of two
/// streams of the same courier. Real Redis: the Lua script's atomicity cannot be shown with a mock.
/// </summary>
public sealed class CurrentPositionTests(TrackingFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IDatabase Db => fixture.Redis.GetDatabase();

    [Fact]
    public async Task Stream_NewerPoints_BecomeTheCurrentPosition()
    {
        var courier = Guid.CreateVersion7();

        var result = await SendAsync(courier, Point(1, 40.990, 29.020), Point(2, 40.995, 29.030));

        result.ShouldSatisfyAllConditions(r => r.Received.ShouldBe(2), r => r.Accepted.ShouldBe(2), r => r.OutOfOrder.ShouldBe(0));
        await ShouldBeAtAsync(courier, 40.995, 29.030, expectedSeq: 2);
    }

    [Fact]
    public async Task Stream_OutOfOrderPoint_DoesNotRollBackTheCurrentPosition()
    {
        var courier = Guid.CreateVersion7();

        // seq 3 arrives before seq 2 (network reordering, a reconnect replaying old points).
        var result = await SendAsync(courier, Point(1, 40.990, 29.020), Point(3, 41.000, 29.040), Point(2, 40.995, 29.030));

        result.ShouldSatisfyAllConditions(r => r.Accepted.ShouldBe(2), r => r.OutOfOrder.ShouldBe(1));
        await ShouldBeAtAsync(courier, 41.000, 29.040, expectedSeq: 3);
    }

    [Fact]
    public async Task Stream_SameSeqTwice_CountsTheRepeatAsNotNewer()
    {
        var courier = Guid.CreateVersion7();

        var result = await SendAsync(courier, Point(5, 40.990, 29.020), Point(5, 40.990, 29.020));

        result.ShouldSatisfyAllConditions(r => r.Accepted.ShouldBe(1), r => r.OutOfOrder.ShouldBe(1));
    }

    [Fact]
    public async Task Stream_AcceptedPoints_ArePublishedOnTheCourierChannel_OldOnesAreNot()
    {
        var courier = Guid.CreateVersion7();
        var queue = await fixture.Redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(CurrentPositionStore.ChannelFor(courier)));

        await SendAsync(courier, Point(1, 40.990, 29.020), Point(2, 40.995, 29.030), Point(1, 40.990, 29.020));

        var first = await ReadSeqsAsync(queue, 2);
        first.ShouldBe([1L, 2L]);
        queue.TryRead(out _).ShouldBeFalse("the stale seq 1 must not be published");
    }

    [Fact]
    public async Task Stream_PositionOfOneCourier_NeverTouchesAnother()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        // The messages carry no courier id at all: the only identity is the token, so this cannot be forged.
        await SendAsync(a, Point(1, 40.990, 29.020));
        await SendAsync(b, Point(1, 41.050, 29.100));

        await ShouldBeAtAsync(a, 40.990, 29.020, expectedSeq: 1);
        await ShouldBeAtAsync(b, 41.050, 29.100, expectedSeq: 1);
    }

    [Fact]
    public async Task Stream_TwoConcurrentStreamsOfOneCourier_NeverMoveTheCurrentPositionBackwards()
    {
        // One round can get lucky with timing; many rounds make a missing atomicity show up.
        for (var round = 0; round < 20; round++)
        {
            var courier = Guid.CreateVersion7();
            var queue = await fixture.Redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(CurrentPositionStore.ChannelFor(courier)));
            var points = Enumerable.Range(1, 100).Select(i => Point(i, 40.9 + (i * 0.0001), 29.0)).ToArray();

            // Two connections of the same courier (a reconnect while the old stream is still draining),
            // both sending seq 1..100, released together.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<StreamLocationsResult> StreamAsync()
            {
                await start.Task;
                return await SendAsync(courier, points);
            }

            var streams = new[] { StreamAsync(), StreamAsync() };
            start.SetResult();
            var results = await Task.WhenAll(streams);

            // Every accepted point was published, in the order Redis applied them. If check-and-write were
            // not one step, one stream's older point could land after the other's newer one.
            var accepted = (int)results.Sum(r => r.Accepted);
            var published = await ReadSeqsAsync(queue, accepted);
            published.ShouldBe([.. published.OrderBy(s => s)], $"round {round}: published sequence went backwards");
            published.ShouldBeUnique($"round {round}");
            await ShouldBeAtAsync(courier, points[^1].Lat, 29.0, expectedSeq: 100);
        }
    }

    [Theory]
    [InlineData(91d, 29d, 1L, true)]            // beyond any latitude
    [InlineData(86d, 29d, 1L, true)]            // valid on Earth, but Redis GEO refuses it
    [InlineData(double.NaN, 29d, 1L, true)]
    [InlineData(40d, 181d, 1L, true)]
    [InlineData(40d, 29d, 0L, true)]            // seq must be positive
    [InlineData(40d, 29d, 1L, false)]           // recorded_at missing
    public async Task Stream_InvalidPoint_EndsTheCallWithInvalidArgument(double lat, double lon, long seq, bool hasRecordedAt)
    {
        var courier = Guid.CreateVersion7();
        var bad = new LocationPoint { Seq = seq, Lat = lat, Lon = lon, RecordedAt = hasRecordedAt ? Timestamp.FromDateTimeOffset(TestClock.Start) : null };

        var failure = await Should.ThrowAsync<RpcException>(() => SendAsync(courier, bad));

        failure.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Stream_ValidPointsBeforeAnInvalidOne_AreKept()
    {
        var courier = Guid.CreateVersion7();

        await Should.ThrowAsync<RpcException>(() => SendAsync(courier, Point(1, 40.990, 29.020), new LocationPoint { Seq = 2, Lat = 999 }));

        await ShouldBeAtAsync(courier, 40.990, 29.020, expectedSeq: 1);
    }

    private async Task<StreamLocationsResult> SendAsync(Guid courier, params LocationPoint[] points)
    {
        using var channel = fixture.Api.CreateGrpcChannel();
        var client = new CourierLocationIngest.CourierLocationIngestClient(channel);
        using var call = client.StreamLocations(TrackingApiFactory.As(DevUsers.Courier(courier)), cancellationToken: Token);
        foreach (var point in points)
        {
            await call.RequestStream.WriteAsync(point, Token);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    private async Task ShouldBeAtAsync(Guid courier, double lat, double lon, long expectedSeq)
    {
        var position = await Db.GeoPositionAsync(CurrentPositionStore.GeoKey, courier.ToString("D"));
        position.ShouldNotBeNull();
        position.Value.Latitude.ShouldBe(lat, 0.0001);
        position.Value.Longitude.ShouldBe(lon, 0.0001);
        ((long)await Db.HashGetAsync(CurrentPositionStore.LastSeqKey, courier.ToString("D"))).ShouldBe(expectedSeq);
    }

    private static async Task<List<long>> ReadSeqsAsync(ChannelMessageQueue queue, int count)
    {
        var seqs = new List<long>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (seqs.Count < count)
        {
            var message = await queue.ReadAsync(limit.Token);
            using var json = System.Text.Json.JsonDocument.Parse(message.Message.ToString());
            seqs.Add(json.RootElement.GetProperty("seq").GetInt64());
        }

        return seqs;
    }

    private static LocationPoint Point(long seq, double lat, double lon) =>
        new() { Seq = seq, Lat = lat, Lon = lon, AccuracyM = 5, RecordedAt = Timestamp.FromDateTimeOffset(TestClock.Start) };
}
