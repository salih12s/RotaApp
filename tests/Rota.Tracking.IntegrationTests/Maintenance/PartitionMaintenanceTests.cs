using Microsoft.Extensions.Options;
using Npgsql;
using Rota.Tracking.Api.Infrastructure;
using Rota.TestKit;

namespace Rota.Tracking.IntegrationTests.Maintenance;

/// <summary>
/// The daily partitions of <c>location_points</c> (PASS 5). Each test owns a fresh database: dropping
/// partitions in the shared one would take away what other tests write into.
/// </summary>
public sealed class PartitionMaintenanceTests(TrackingFixture fixture)
{
    private static readonly string[] _firstWindow =
        ["location_points_20261110", "location_points_20261111", "location_points_20261112", "location_points_20261113", "location_points_20261114"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_OnAnEmptyDatabase_CreatesYesterdayTodayAndTheNextThreeDays()
    {
        await using var database = await NewDatabaseAsync();

        var report = await Maintainer(database).RunAsync(TestClock.Start, Token);

        report.Created.Order().ShouldBe(_firstWindow);
        report.Dropped.ShouldBeEmpty();
        (await PartitionsAsync(database)).ShouldBe(_firstWindow);
    }

    [Fact]
    public async Task Run_Twice_ChangesNothingTheSecondTime()
    {
        await using var database = await NewDatabaseAsync();
        var maintainer = Maintainer(database);
        await maintainer.RunAsync(TestClock.Start, Token);

        var second = await maintainer.RunAsync(TestClock.Start, Token);

        second.Created.ShouldBeEmpty();
        second.Dropped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_AMonthLater_CreatesNewPartitions_AndDropsThoseOlderThanThirtyDays_WithTheirData()
    {
        await using var database = await NewDatabaseAsync();
        var maintainer = Maintainer(database);
        await maintainer.RunAsync(TestClock.Start, Token);
        var courier = Guid.CreateVersion7();
        await InsertPointAsync(database, courier, recordedAt: TestClock.Start);
        (await CountAsync(database)).ShouldBe(1);

        // The calendar moves on by 39 days: the first window is far past the 30-day retention.
        var report = await maintainer.RunAsync(TestClock.Start.AddDays(39), Token);

        report.Dropped.Order().ShouldBe(_firstWindow);
        report.Created.Count.ShouldBe(5);
        (await PartitionsAsync(database)).ShouldAllBe(name => string.CompareOrdinal(name, "location_points_20261219") >= 0);
        (await CountAsync(database)).ShouldBe(0, "dropping the partition took the old points with it, at once");
    }

    [Fact]
    public async Task Run_KeepsAPartitionWhoseDayIsNotYetThirtyDaysOld()
    {
        await using var database = await NewDatabaseAsync();
        var maintainer = Maintainer(database);
        await maintainer.RunAsync(TestClock.Start, Token);

        // 11 Dec 10:00: cutoff is 11 Nov 10:00, so the 10 Nov partition (ends 11 Nov 00:00) goes, 11 Nov stays.
        var report = await maintainer.RunAsync(TestClock.Start.AddDays(30), Token);

        report.Dropped.ShouldBe(["location_points_20261110"]);
        (await PartitionsAsync(database)).Contains("location_points_20261111").ShouldBeTrue("the 11 Nov partition still holds data newer than the cutoff");
    }

    [Fact]
    public async Task Run_TwoInstancesAtTheSameTime_DoNotCollide()
    {
        await using var database = await NewDatabaseAsync();
        var one = Maintainer(database);
        var two = Maintainer(database);

        // Without the advisory lock both would try to create the same partition and one would fail.
        var reports = await Task.WhenAll(one.RunAsync(TestClock.Start, Token), two.RunAsync(TestClock.Start, Token));

        // Each partition was created exactly once, by whichever instance got the lock first.
        reports.SelectMany(r => r.Created).Order().ShouldBe(_firstWindow);
        (await PartitionsAsync(database)).ShouldBe(_firstWindow);
    }

    [Fact]
    public async Task ServiceStartup_CreatesThePartitions_BeforeAnyTraffic()
    {
        var connectionString = await fixture.CreateDatabaseAsync(Token);
        await using var api = new TrackingApiFactory(fixture.RedisConnectionString, connectionString);

        using var client = api.CreateClient();   // building the host starts the hosted services

        await using var database = NpgsqlDataSource.Create(connectionString);
        (await PartitionsAsync(database)).ShouldBe(_firstWindow);
    }

    private static PartitionMaintainer Maintainer(NpgsqlDataSource database) => new(database, Options.Create(new TrackingOptions()));

    private async Task<NpgsqlDataSource> NewDatabaseAsync() => NpgsqlDataSource.Create(await fixture.CreateDatabaseAsync(Token));

    private static async Task<List<string>> PartitionsAsync(NpgsqlDataSource database)
    {
        await using var command = database.CreateCommand(
            """
            SELECT c.relname FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'location_points' ORDER BY c.relname
            """);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task InsertPointAsync(NpgsqlDataSource database, Guid courier, DateTimeOffset recordedAt)
    {
        await using var command = database.CreateCommand(
            "INSERT INTO location_points (courier_id, seq, lat, lon, recorded_at, received_at) VALUES (@c, 1, 40.99, 29.02, @t, @t)");
        command.Parameters.AddWithValue("c", courier);
        command.Parameters.AddWithValue("t", recordedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> CountAsync(NpgsqlDataSource database)
    {
        await using var command = database.CreateCommand("SELECT count(*) FROM location_points");
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}
