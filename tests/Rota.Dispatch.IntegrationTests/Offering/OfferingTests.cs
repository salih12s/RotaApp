using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Offering;

namespace Rota.Dispatch.IntegrationTests.Offering;

/// <summary>
/// One search step for one job against a real PostgreSQL and a fake Tracking server. Integration tests, because the
/// reservation is a conditional UPDATE whose behaviour only the real engine shows, and the deadline only a real gRPC channel.
/// </summary>
public sealed class OfferingTests(DispatchFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryOffer_WithAvailableCourier_CreatesPendingOfferAndReservesCourier()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 1_000)];

        var outcome = await s.OfferAsync(orderId, Cancel);

        outcome.ShouldBe(OfferingOutcome.Offered);
        var offer = (await s.GetOffersOfOrderAsync(orderId)).ShouldHaveSingleItem();
        offer.Status.ShouldBe(OfferStatus.Pending);
        offer.CourierId.ShouldBe(courierId);
        offer.DistanceMeters.ShouldBe(1_000);
        offer.EtaSeconds.ShouldBe(180, "1 km at 20 km/h is 3 minutes (ADR-008)");
        offer.ExpiresAt.ShouldBe(s.Time.GetUtcNow() + DispatchScenario.Rules.OfferTtl);

        var courier = await s.GetCourierAsync(courierId);
        courier.State.ShouldBe(CourierState.Offered);
        courier.CurrentOfferId.ShouldBe(offer.Id);
        courier.CurrentOrderId.ShouldBe(orderId);

        var job = await s.GetJobAsync(orderId);
        job.Status.ShouldBe(DispatchJobStatus.Offering);
        job.OfferAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task TryOffer_WritesCourierStateChangedToOutboxInTheSameTransaction()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 500)];

        await s.OfferAsync(orderId, Cancel);

        var message = (await s.GetOutboxAsync("CourierStateChanged.v1")).ShouldHaveSingleItem();
        var data = DispatchScenario.DataOf(message);
        data.GetProperty("courierId").GetGuid().ShouldBe(courierId);
        data.GetProperty("state").GetString().ShouldBe("Offered");
    }

    [Fact]
    public async Task TryOffer_PicksNearestAvailableCourier_AndSkipsThoseWhoAreNotAvailable()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var nearButOffline = await s.SeedCourierAsync(CourierState.Offline);
        var nearest = await s.SeedCourierAsync();
        var farther = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(farther, 2_000), s.Near(nearButOffline, 100), s.Near(nearest, 800)];

        await s.OfferAsync(orderId, Cancel);

        (await s.GetOffersOfOrderAsync(orderId)).ShouldHaveSingleItem().CourierId.ShouldBe(nearest);
        (await s.GetCourierAsync(farther)).State.ShouldBe(CourierState.Available);
        (await s.GetCourierAsync(nearButOffline)).State.ShouldBe(CourierState.Offline);
    }

    [Fact]
    public async Task TryOffer_IgnoresCouriersDispatchDoesNotKnow()
    {
        // Tracking sees positions of anyone with the app open; only a courier who started a shift exists in Dispatch.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(Guid.CreateVersion7(), 300)];

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.NoCandidates);
    }

    [Fact]
    public async Task TryOffer_WhenCandidateWasTakenMeanwhile_FallsBackToTheNextOne()
    {
        // The nearest courier is already holding another order's offer (state Offered in our table, Tracking cannot know).
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var busy = await s.SeedPendingOfferAsync();
        var free = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(busy.CourierId, 100), s.Near(free, 900)];

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered);

        (await s.GetOffersOfOrderAsync(orderId)).ShouldHaveSingleItem().CourierId.ShouldBe(free);
    }

    [Fact]
    public async Task TryOffer_WhenNobodyIsNear_SchedulesRetryAfterTwentySeconds()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.NoCandidates);

        var job = await s.GetJobAsync(orderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.NextSearchAt.ShouldBe(s.Time.GetUtcNow() + DispatchScenario.Rules.SearchRetryInterval);
        (await s.GetOutboxAsync("DispatchFailed.v1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TryOffer_WhenNobodyIsNearAfterTheSearchTimeout_GivesUpAndReportsDispatchFailed()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();
        s.Time.Advance(DispatchScenario.Rules.SearchTimeout);

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.NoCandidates);

        (await s.GetJobAsync(orderId)).Status.ShouldBe(DispatchJobStatus.Unassigned);
        var failed = (await s.GetOutboxAsync("DispatchFailed.v1")).ShouldHaveSingleItem();
        DispatchScenario.DataOf(failed).GetProperty("reason").GetString().ShouldBe("NoCandidates");
    }

    [Fact]
    public async Task TryOffer_ForACancelledJob_DoesNothing()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 500)];
        await s.ExecuteAsync($"UPDATE dispatch_jobs SET status = 'Cancelled' WHERE order_id = {orderId}");

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Skipped);

        (await s.GetOffersOfOrderAsync(orderId)).ShouldBeEmpty();
        (await s.GetCourierAsync(courierId)).State.ShouldBe(CourierState.Available);
        s.Tracking.State.Calls.ShouldBe(0, "no point asking Tracking about a job nobody wants");
    }

    // ---- Tracking is slow or down (docs/06 §6, F05 PASS 5 and 6) -------------------------------------------------------

    [Fact]
    public async Task TryOffer_WhenTrackingIsSlow_GivesUpAtTheDeadlineAndKeepsTheJobSearching()
    {
        var settings = new Dictionary<string, string> { ["Dispatch:NearbyDeadline"] = "00:00:00.200" }; // the production value
        await using var s = await DispatchScenario.StartAsync(fixture, settings, Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 500)];
        s.Tracking.State.Delay = TimeSpan.FromSeconds(1);

        var clock = Stopwatch.StartNew();
        var outcome = await s.OfferAsync(orderId, Cancel);
        clock.Stop();

        outcome.ShouldBe(OfferingOutcome.Postponed);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(800), "the 200 ms deadline must cut the 1 s answer short");

        var job = await s.GetJobAsync(orderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.OfferAttempts.ShouldBe(0, "a slow Tracking is not a failed offer");
        job.NextSearchAt.ShouldBe(s.Time.GetUtcNow() + DispatchScenario.Rules.SearchRetryInterval);
        (await s.GetOffersOfOrderAsync(orderId)).ShouldBeEmpty();
        (await s.GetCourierAsync(courierId)).State.ShouldBe(CourierState.Available);
    }

    [Fact]
    public async Task TryOffer_WhenTrackingIsUnavailable_PostponesThenAssignsOnceTrackingIsBack_WithoutDoubleAssignment()
    {
        // Stand-in for "Redis is down": Tracking answers UNAVAILABLE exactly as it does when its Redis is unreachable.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 500)];
        s.Tracking.State.FailWith = _ => StatusCode.Unavailable;

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Postponed);
        (await s.GetOffersOfOrderAsync(orderId)).ShouldBeEmpty();

        // Tracking comes back; the postponed time passes; the next round finds the job due.
        s.Tracking.State.FailWith = null;
        s.Time.Advance(DispatchScenario.Rules.SearchRetryInterval);
        await using (var scope = s.Scope())
        {
            var rounds = scope.ServiceProvider.GetRequiredService<DispatchRounds>();
            (await rounds.SearchOnceAsync(Cancel)).ShouldBe(1);
        }

        (await s.GetOffersOfOrderAsync(orderId)).ShouldHaveSingleItem().CourierId.ShouldBe(courierId);
        (await s.CountAsync($"""SELECT count(*)::int AS "Value" FROM offers WHERE status = 'Pending'""")).ShouldBe(1);
    }

    [Fact]
    public async Task TryOffer_WhenTrackingFailsOnceWithUnavailable_RetriesExactlyOnceAndSucceeds()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 500)];
        s.Tracking.State.FailWith = call => call == 1 ? StatusCode.Unavailable : null;

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered);

        s.Tracking.State.Calls.ShouldBe(2, "one failed attempt plus one retry");
    }

    [Fact]
    public async Task TryOffer_WhenTrackingKeepsFailingWithUnavailable_RetriesOnlyOnce()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.FailWith = _ => StatusCode.Unavailable;

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Postponed);

        s.Tracking.State.Calls.ShouldBe(2, "docs/05: one retry, then the job is postponed");
    }

    [Fact]
    public async Task TryOffer_WhenTrackingFailsWithOtherError_DoesNotRetry()
    {
        // Only UNAVAILABLE is safe to repeat blindly; any other failure is a bug to surface, not to hammer.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.FailWith = _ => StatusCode.InvalidArgument;

        await Should.ThrowAsync<RpcException>(() => s.OfferAsync(orderId, Cancel));

        s.Tracking.State.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TryOffer_IdentifiesItselfToTrackingAsAService()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var orderId = await s.SeedJobAsync();

        await s.OfferAsync(orderId, Cancel);

        s.Tracking.State.LastCaller.ShouldBe("service:dispatch");
    }
}
