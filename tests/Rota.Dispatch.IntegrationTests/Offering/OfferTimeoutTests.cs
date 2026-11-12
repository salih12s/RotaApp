using Microsoft.Extensions.DependencyInjection;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Offering;

namespace Rota.Dispatch.IntegrationTests.Offering;

/// <summary>An offer nobody answers: the clock moves, the timeout round runs, the job moves on. Time is the fake clock, so nothing waits 30 seconds.</summary>
public sealed class OfferTimeoutTests(DispatchFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<int> ExpireAsync(DispatchScenario s, CancellationToken cancellationToken)
    {
        await using var scope = s.Scope();
        return await scope.ServiceProvider.GetRequiredService<DispatchRounds>().ExpireOnceAsync(cancellationToken);
    }

    [Fact]
    public async Task Expire_AfterTheTtl_ClosesTheOfferFreesTheCourierAndSendsTheJobBackToSearching()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        s.Time.Advance(DispatchScenario.Rules.OfferTtl);

        (await ExpireAsync(s, Cancel)).ShouldBe(1);

        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Expired);
        (await s.GetCourierAsync(seeded.CourierId)).State.ShouldBe(CourierState.Available);
        var job = await s.GetJobAsync(seeded.OrderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.NextSearchAt.ShouldBeNull("the next candidate is tried at once, not after the 20 s 'nobody found' wait");
    }

    [Fact]
    public async Task Expire_OneSecondBeforeTheTtl_LeavesTheOfferAlone()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        s.Time.Advance(DispatchScenario.Rules.OfferTtl - TimeSpan.FromSeconds(1));

        (await ExpireAsync(s, Cancel)).ShouldBe(0);

        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Pending);
        (await s.GetCourierAsync(seeded.CourierId)).State.ShouldBe(CourierState.Offered);
    }

    [Fact]
    public async Task Expire_ExactlyAtTheTtl_ClosesTheOffer()
    {
        // Edge case: the boundary. "expires_at <= now" means an offer expiring at 10:00:30 is dead at 10:00:30 sharp.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        s.Time.SetUtcNow(seeded.ExpiresAt);

        (await ExpireAsync(s, Cancel)).ShouldBe(1);
    }

    [Fact]
    public async Task Expire_OnTheLastAttempt_UnassignsTheJobAndReportsDispatchFailed()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync(attempt: DispatchScenario.Rules.MaxOfferAttempts);
        s.Time.Advance(DispatchScenario.Rules.OfferTtl);

        await ExpireAsync(s, Cancel);

        (await s.GetJobAsync(seeded.OrderId)).Status.ShouldBe(DispatchJobStatus.Unassigned);
        DispatchScenario.DataOf((await s.GetOutboxAsync("DispatchFailed.v1")).ShouldHaveSingleItem())
            .GetProperty("reason").GetString().ShouldBe("MaxAttemptsReached");
    }

    [Fact]
    public async Task Expire_RunTwice_SecondRunDoesNothing()
    {
        // Idempotency: a worker that restarts or two ticks that overlap must not release the courier again.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        s.Time.Advance(DispatchScenario.Rules.OfferTtl);

        await ExpireAsync(s, Cancel);
        var versionAfterFirst = (await s.GetCourierAsync(seeded.CourierId)).Version;
        (await ExpireAsync(s, Cancel)).ShouldBe(0);

        (await s.GetCourierAsync(seeded.CourierId)).Version.ShouldBe(versionAfterFirst);
    }

    [Fact]
    public async Task Expire_ThenSearch_OffersTheOrderToTheNextCourierNotTheSameOneAgain()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var ignoresOffers = await s.SeedCourierAsync();
        var next = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(ignoresOffers, 100), s.Near(next, 900)];

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered);
        (await s.GetOffersOfOrderAsync(orderId)).ShouldHaveSingleItem().CourierId.ShouldBe(ignoresOffers);

        s.Time.Advance(DispatchScenario.Rules.OfferTtl);
        await ExpireAsync(s, Cancel);
        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered);

        var offers = await s.GetOffersOfOrderAsync(orderId);
        offers.Count.ShouldBe(2);
        offers.Single(o => o.Status == OfferStatus.Pending).CourierId.ShouldBe(next);
        (await s.GetJobAsync(orderId)).OfferAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task FiveUnansweredOffers_EndWithDispatchFailedAfterTheFifthCourier()
    {
        // The whole give-up path in one story: five different couriers ignore the order; the sixth search finds nobody new.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var couriers = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            couriers.Add(await s.SeedCourierAsync());
        }

        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [.. couriers.Select((id, index) => s.Near(id, (index + 1) * 100))];

        for (var attempt = 1; attempt <= DispatchScenario.Rules.MaxOfferAttempts; attempt++)
        {
            (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered, $"attempt {attempt}");
            s.Time.Advance(DispatchScenario.Rules.OfferTtl);
            await ExpireAsync(s, Cancel);
        }

        var job = await s.GetJobAsync(orderId);
        job.Status.ShouldBe(DispatchJobStatus.Unassigned);
        job.OfferAttempts.ShouldBe(5);
        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Skipped, "an unassigned job is not searched any more");
        (await s.GetOutboxAsync("DispatchFailed.v1")).Count.ShouldBe(1);
    }
}
