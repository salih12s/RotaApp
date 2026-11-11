using Grpc.Core;
using Rota.Tracking.Api.Infrastructure;
using Rota.Tracking.IntegrationTests.Support;
using Rota.TestKit;
using StackExchange.Redis;

namespace Rota.Tracking.IntegrationTests.Ingest;

/// <summary>The position history in PostgreSQL: every valid point is kept, repeats are not, writing is batched.</summary>
public sealed class HistoryTests(TrackingFixture fixture)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- R-8, second half: the old point is not the current position, but it is in the history ----

    [Fact]
    public async Task OutOfOrderPoint_IsWrittenToHistory_ButDoesNotMoveTheCurrentPosition()
    {
        var courier = Guid.CreateVersion7();

        var result = await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(1, 40.990), Streams.Point(3, 41.000), Streams.Point(2, 40.995));

        result.OutOfOrder.ShouldBe(1);
        await WaitForRowsAsync(courier, 3);
        (await SeqsAsync(courier)).ShouldBe([1L, 2L, 3L]);
        ((long)await fixture.Redis.GetDatabase().HashGetAsync(CurrentPositionStore.LastSeqKey, courier.ToString("D"))).ShouldBe(3);
    }

    // ---- PASS 2: the same point twice is one row ----

    [Fact]
    public async Task SamePointSentTwice_IsOneRowInHistory()
    {
        var courier = Guid.CreateVersion7();

        // Twice in one stream, and again on a new connection (a client resending after a dropped call).
        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(5), Streams.Point(5));
        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(5));

        // Batches are written in order, so once this later point is stored, the repeats were handled too.
        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(6));
        await WaitForRowsAsync(courier, 2);

        (await SeqsAsync(courier)).ShouldBe([5L, 6L]);
    }

    [Fact]
    public async Task ManyPoints_MoreThanOneBatch_AreAllWritten()
    {
        var courier = Guid.CreateVersion7();
        const int count = 2_500; // batch size is 1000, so this takes at least three batches
        var points = Enumerable.Range(1, count).Select(i => Streams.Point(i, 40.9 + (i * 0.00001))).ToArray();

        var result = await Streams.SendAsync(fixture.Api, courier, Token, points);

        result.Received.ShouldBe(count);
        await WaitForRowsAsync(courier, count);
    }

    [Fact]
    public async Task Accuracy_ZeroMeansNotReported_AndIsStoredAsNull()
    {
        var courier = Guid.CreateVersion7();

        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(1, accuracy: 0), Streams.Point(2, accuracy: 7.5f));

        await WaitForRowsAsync(courier, 2);
        await using var command = fixture.Database.CreateCommand("SELECT accuracy_m FROM location_points WHERE courier_id = @id ORDER BY seq");
        command.Parameters.AddWithValue("id", courier);
        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();
        reader.IsDBNull(0).ShouldBeTrue();
        (await reader.ReadAsync(Token)).ShouldBeTrue();
        reader.GetFloat(0).ShouldBe(7.5f);
    }

    // ---- Which device times are accepted (the history partitions only cover a window) ----

    [Fact]
    public async Task Point_RecordedWithinTheLastDay_LandsInYesterdaysPartition()
    {
        var courier = Guid.CreateVersion7();
        var yesterday = TestClock.Start.AddHours(-23);   // 10 Nov 11:00 while "now" is 11 Nov 10:00

        await Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(1, recordedAt: yesterday));

        await WaitForRowsAsync(courier, 1);
        await using var command = fixture.Database.CreateCommand("SELECT tableoid::regclass::text FROM location_points WHERE courier_id = @id");
        command.Parameters.AddWithValue("id", courier);
        (await command.ExecuteScalarAsync(Token)).ShouldBe("location_points_20261110");
    }

    [Theory]
    [InlineData(-25, "older than")]   // 25 hours old: its day may have no partition any more
    [InlineData(1, "future")]         // an hour ahead: a broken device clock
    public async Task Point_OutsideTheAcceptedWindow_IsInvalidArgument(int hoursFromNow, string reason)
    {
        var courier = Guid.CreateVersion7();

        var failure = await Should.ThrowAsync<RpcException>(
            () => Streams.SendAsync(fixture.Api, courier, Token, Streams.Point(1, recordedAt: TestClock.Start.AddHours(hoursFromNow))));

        failure.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        failure.Status.Detail.ShouldContain(reason);
    }

    // ---- Priorities: dispatching needs the current position; history is the part that may give way ----

    [Fact]
    public async Task WhenTheDatabaseIsDown_TheCurrentPositionIsStillUpdated()
    {
        // Nothing listens on port 1. The service still starts and the stream still works: history is
        // given up on (and counted), but couriers keep being found.
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, "Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var courier = Guid.CreateVersion7();

        var result = await Streams.SendAsync(api, courier, Token, Streams.Point(1, 40.990), Streams.Point(2, 40.995));

        result.Accepted.ShouldBe(2);
        var position = await fixture.Redis.GetDatabase().GeoPositionAsync(CurrentPositionStore.GeoKey, courier.ToString("D"));
        position.ShouldNotBeNull().Latitude.ShouldBe(40.995, 0.0001);
    }

    private async Task WaitForRowsAsync(Guid courier, int expected) =>
        await Eventually.UntilAsync(async () => await CountAsync(courier) >= expected, _wait, $"{expected} history rows of {courier}", Token);

    private async Task<long> CountAsync(Guid courier)
    {
        await using var command = fixture.Database.CreateCommand("SELECT count(*) FROM location_points WHERE courier_id = @id");
        command.Parameters.AddWithValue("id", courier);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private async Task<List<long>> SeqsAsync(Guid courier)
    {
        await using var command = fixture.Database.CreateCommand("SELECT seq FROM location_points WHERE courier_id = @id ORDER BY seq");
        command.Parameters.AddWithValue("id", courier);
        var seqs = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs;
    }
}
