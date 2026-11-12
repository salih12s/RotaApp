using System.Net;
using System.Net.Http.Json;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests.Features;

public sealed class ShiftTests(DispatchFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartShift_FirstTime_RegistersTheCourierAndMakesThemAvailable()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = Guid.CreateVersion7();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId))
            .PostAsJsonAsync("/api/v1/couriers/me/shift/start", new { displayName = "Ayşe Yılmaz" }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("state").GetString().ShouldBe("Available");
        body.GetProperty("displayName").GetString().ShouldBe("Ayşe Yılmaz");

        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.Available);
        courier.ShiftStartedAt.ShouldBe(s.Time.GetUtcNow());
        DispatchScenario.DataOf((await s.GetOutboxAsync("CourierStateChanged.v1")).ShouldHaveSingleItem())
            .GetProperty("state").GetString().ShouldBe("Available");
    }

    [Fact]
    public async Task StartShift_WithoutBody_UsesAGeneratedName()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = Guid.CreateVersion7();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId)).PostAsync("/api/v1/couriers/me/shift/start", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.GetCourierAsync(courierId)).DisplayName.ShouldStartWith("Courier ");
    }

    [Fact]
    public async Task StartShift_WithTooLongName_Returns400()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()))
            .PostAsJsonAsync("/api/v1/couriers/me/shift/start", new { displayName = new string('x', 101) }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartShift_WhenAlreadyOnShift_Returns409WithTheCurrentState()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId)).PostAsync("/api/v1/couriers/me/shift/start", content: null, Cancel);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-courier-transition");
        problem.GetProperty("currentState").GetString().ShouldBe("Available");
    }

    [Fact]
    public async Task StartShift_AsDispatcher_Returns403()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(DevUsers.Dispatcher("dsp-1")).PostAsync("/api/v1/couriers/me/shift/start", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StartShift_ByTheSameCourierConcurrently_StartsExactlyOnce()
    {
        // Two taps on "start shift" at once (a flaky connection, a retry): one wins, the other gets a clean 409.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = Guid.CreateVersion7();
        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return (await s.Api.CreateClientAs(DevUsers.Courier(courierId)).PostAsync("/api/v1/couriers/me/shift/start", content: null, Cancel)).StatusCode;
        })).ToList();
        gate.SetResult();

        var statuses = await Task.WhenAll(calls);

        statuses.Count(c => c == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(c => c == HttpStatusCode.Conflict).ShouldBe(7);
        (await s.GetOutboxAsync("CourierStateChanged.v1")).Count.ShouldBe(1);
        (await s.GetCourierAsync(courierId)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task EndShift_WhenAvailable_MakesTheCourierOffline()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId)).PostAsync("/api/v1/couriers/me/shift/end", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.Offline);
        courier.ShiftStartedAt.ShouldBeNull();
    }

    [Fact]
    public async Task EndShift_WhileHoldingAnOffer_Returns409CourierBusy()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId)).PostAsync("/api/v1/couriers/me/shift/end", content: null, Cancel);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "courier-busy");
        problem.GetProperty("currentState").GetString().ShouldBe("Offered");
        (await s.GetCourierAsync(seeded.CourierId)).State.ShouldBe(CourierState.Offered);
    }

    [Fact]
    public async Task EndShift_WhileOnDelivery_Returns409CourierBusy()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (_, courierId) = await s.SeedOnDeliveryAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId)).PostAsync("/api/v1/couriers/me/shift/end", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "courier-busy");
    }

    [Fact]
    public async Task EndShift_ForACourierWhoNeverStarted_Returns409()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7())).PostAsync("/api/v1/couriers/me/shift/end", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid-courier-transition");
    }
}
