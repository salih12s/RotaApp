using System.Net;
using System.Net.Http.Json;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests.Features;

/// <summary>What a dispatcher can do by hand: list couriers, assign one to an order, restart a job that gave up.</summary>
public sealed class DispatcherCommandTests(DispatchFixture fixture)
{
    private static readonly string _dispatcher = DevUsers.Dispatcher("dsp-1");
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Manual assignment ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ManualAssign_AvailableCourierToSearchingJob_AssignsAndReportsManualAssignment()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Completed);
        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.OnDelivery);
        courier.CurrentOrderId.ShouldBe(orderId);

        var assigned = DispatchScenario.DataOf((await s.GetOutboxAsync("CourierAssigned.v1")).ShouldHaveSingleItem());
        assigned.GetProperty("assignmentType").GetString().ShouldBe("Manual");
        assigned.GetProperty("courierId").GetGuid().ShouldBe(courierId);
        assigned.GetProperty("offerId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task ManualAssign_ToAnUnassignedJob_Works()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        await s.ExecuteAsync($"UPDATE dispatch_jobs SET status = 'Unassigned' WHERE order_id = {orderId}");

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManualAssign_BusyCourier_Returns409CourierNotAvailableAndChangesNothing()
    {
        // F05 PASS 7. The job update runs first inside the same transaction, so this also proves it is rolled back.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (_, busyCourier) = await s.SeedOnDeliveryAsync();
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId = busyCourier }, Cancel);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "courier-not-available");
        problem.GetProperty("currentState").GetString().ShouldBe("OnDelivery");
        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Searching, "the job must stay assignable");
        (await s.GetOutboxAsync("CourierAssigned.v1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ManualAssign_OfflineCourier_Returns409CourierNotAvailable()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync(CourierState.Offline);
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "courier-not-available");
    }

    [Fact]
    public async Task ManualAssign_CourierHoldingAnOffer_Returns409CourierNotAvailable()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var offered = await s.SeedPendingOfferAsync();
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId = offered.CourierId }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "courier-not-available");
    }

    [Fact]
    public async Task ManualAssign_UnknownCourier_Returns404()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId = Guid.CreateVersion7() }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Searching);
    }

    [Fact]
    public async Task ManualAssign_UnknownJob_Returns404()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{Guid.CreateVersion7()}/manual-assign", new { courierId }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await s.GetCourierAsync(courierId)).State.ShouldBe(CourierState.Available);
    }

    [Fact]
    public async Task ManualAssign_ToAJobThatIsAlreadyCompleted_Returns409JobNotAssignableAndKeepsTheCourierFree()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var (completedOrder, _) = await s.SeedOnDeliveryAsync();
        var freeCourier = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{completedOrder}/manual-assign", new { courierId = freeCourier }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "job-not-assignable");
        (await s.GetCourierAsync(freeCourier)).State.ShouldBe(CourierState.Available);
    }

    [Fact]
    public async Task ManualAssign_WhileAnOfferIsOpen_Returns409JobNotAssignable()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        var other = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{seeded.OrderId}/manual-assign", new { courierId = other }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "job-not-assignable");
        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Pending);
    }

    [Fact]
    public async Task ManualAssign_TwoDispatchersForTheSameCourierAtOnce_OnlyOneOrderGetsHim()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orders = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await s.SeedJobAsync());
        }

        var gate = new TaskCompletionSource();
        var calls = orders.Select(orderId => Task.Run(async () =>
        {
            await gate.Task;
            return (await s.Api.CreateClientAs(_dispatcher)
                .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Cancel)).StatusCode;
        })).ToList();
        gate.SetResult();

        var statuses = await Task.WhenAll(calls);

        statuses.Count(c => c == HttpStatusCode.OK).ShouldBe(1);
        statuses.Count(c => c == HttpStatusCode.Conflict).ShouldBe(5);
        (await s.CountAsync($"""SELECT count(*)::int AS "Value" FROM dispatch_jobs WHERE status = 'Completed'""")).ShouldBe(1, "the losers' job updates must have been rolled back");
    }

    [Fact]
    public async Task ManualAssign_AsCourier_Returns403()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId))
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId }, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ManualAssign_WithEmptyCourierId_Returns400()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher)
            .PostAsJsonAsync($"/api/v1/dispatch-jobs/{orderId}/manual-assign", new { courierId = Guid.Empty }, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
    }

    // ---- Restart -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restart_UnassignedJob_SearchesAgainFromScratchAndTellsOrders()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync(attempt: DispatchScenario.Rules.MaxOfferAttempts);
        await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId)).PostAsync($"/api/v1/offers/{seeded.OfferId}/reject", content: null, Cancel);
        (await s.GetJobAsync(seeded.OrderId)).Status.ShouldBe(DispatchJobStatus.Unassigned);
        s.Time.Advance(TimeSpan.FromMinutes(10)); // the dispatcher notices later

        var response = await s.Api.CreateClientAs(_dispatcher).PostAsync($"/api/v1/dispatch-jobs/{seeded.OrderId}/restart", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = await s.GetJobAsync(seeded.OrderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.OfferAttempts.ShouldBe(0);
        job.SearchStartedAt.ShouldBe(s.Time.GetUtcNow(), "the 3-minute search budget starts over");
        DispatchScenario.DataOf((await s.GetOutboxAsync("DispatchStarted.v1")).ShouldHaveSingleItem())
            .GetProperty("orderId").GetGuid().ShouldBe(seeded.OrderId);
    }

    [Fact]
    public async Task Restart_JobThatIsStillSearching_Returns409()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        var response = await s.Api.CreateClientAs(_dispatcher).PostAsync($"/api/v1/dispatch-jobs/{orderId}/restart", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "job-not-assignable");
    }

    [Fact]
    public async Task Restart_UnknownJob_Returns404()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(_dispatcher).PostAsync($"/api/v1/dispatch-jobs/{Guid.CreateVersion7()}/restart", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    // ---- Listing couriers ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListCouriers_FilteredByState_ReturnsOnlyThatState()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var available = await s.SeedCourierAsync();
        await s.SeedCourierAsync(CourierState.Offline);
        await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(_dispatcher).GetAsync("/api/v1/couriers?state=Available", Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([available]);
    }

    [Fact]
    public async Task ListCouriers_WithoutFilter_ReturnsEveryone()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SeedCourierAsync();
        await s.SeedCourierAsync(CourierState.Offline);
        await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(_dispatcher).GetAsync("/api/v1/couriers", Cancel);

        (await response.ReadJsonAsync()).GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task ListCouriers_WithUnknownState_Returns400()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(_dispatcher).GetAsync("/api/v1/couriers?state=Sleeping", Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task ListCouriers_AsCourier_Returns403()
    {
        // A courier must not be able to list the other couriers.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7())).GetAsync("/api/v1/couriers", Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
