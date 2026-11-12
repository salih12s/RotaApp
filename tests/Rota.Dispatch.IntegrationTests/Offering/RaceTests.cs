using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.IntegrationTests.Offering;

/// <summary>
/// The tests that justify the whole design: many requests at the same instant against a real PostgreSQL. A mock cannot show these
/// bugs, because they live in how the engine orders concurrent statements. Each runs several rounds: a race that goes wrong once in
/// twenty runs is still a bug, and one lucky run proves nothing.
/// <para>Run only these: <c>dotnet test tests/Rota.Dispatch.IntegrationTests --filter-trait "Category=Race"</c></para>
/// </summary>
[Trait("Category", "Race")]
public sealed class RaceTests(DispatchFixture fixture)
{
    private const int Rounds = 20;
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R1_HundredConcurrentOrdersAndTenCouriers_NeverGiveAnyoneTwoOffersOrAnyOrderTwoCouriers()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        for (var round = 1; round <= Rounds; round++)
        {
            var couriers = new List<Guid>();
            for (var i = 0; i < 10; i++)
            {
                couriers.Add(await s.SeedCourierAsync());
            }

            var orders = new List<Guid>();
            for (var i = 0; i < 100; i++)
            {
                orders.Add(await s.SeedJobAsync());
            }

            // Every search sees the same ten couriers in the same order, so all hundred jobs fight for courier #1 first.
            s.Tracking.State.Couriers = [.. couriers.Select((id, index) => s.Near(id, (index + 1) * 100))];

            var gate = new TaskCompletionSource();
            var searches = orders.Select(orderId => Task.Run(async () =>
            {
                await gate.Task;
                return await s.OfferAsync(orderId, Cancel);
            })).ToList();
            gate.SetResult();
            var outcomes = await Task.WhenAll(searches);

            // Ten couriers can serve ten orders; the other ninety find nobody free (and know it only after trying all ten).
            outcomes.Count(o => o == OfferingOutcome.Offered).ShouldBe(10, $"round {round}");
            outcomes.Count(o => o == OfferingOutcome.NoCandidates).ShouldBe(90, $"round {round}");

            await AssertNoDoubleBookingAsync(s, $"after offering, round {round}");
            (await s.CountAsync($"""SELECT count(*)::int AS "Value" FROM couriers WHERE state = 'Offered'""")).ShouldBe(10, $"round {round}");

            // Phase two: every courier accepts at the same instant.
            var offers = await s.QueryAsync(db => db.Offers.AsNoTracking().Where(o => o.Status == OfferStatus.Pending).ToListAsync(Cancel));
            offers.Count.ShouldBe(10, $"round {round}");

            var acceptGate = new TaskCompletionSource();
            var accepts = offers.Select(offer => Task.Run(async () =>
            {
                await acceptGate.Task;
                await using var scope = s.Scope();
                return await scope.ServiceProvider.GetRequiredService<OfferResponseService>().AcceptAsync(offer.Id, offer.CourierId, Cancel);
            })).ToList();
            acceptGate.SetResult();
            (await Task.WhenAll(accepts)).ShouldAllBe(r => r.IsSuccess);

            await AssertNoDoubleBookingAsync(s, $"after accepting, round {round}");
            (await s.CountAsync($"""SELECT count(*)::int AS "Value" FROM couriers WHERE state = 'OnDelivery'""")).ShouldBe(10 * round, $"round {round}");
        }
    }

    // I-1 and I-2, stated in SQL so no application code is trusted to check itself.
    private static async Task AssertNoDoubleBookingAsync(DispatchScenario s, string when)
    {
        (await s.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT courier_id FROM offers WHERE status IN ('Pending', 'Accepted') GROUP BY courier_id HAVING count(*) > 1) t
            """)).ShouldBe(0, $"a courier has two active offers/orders {when}");

        (await s.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT order_id FROM offers WHERE status IN ('Pending', 'Accepted') GROUP BY order_id HAVING count(*) > 1) t
            """)).ShouldBe(0, $"an order has two active offers/couriers {when}");

        // The courier's own row agrees with the offers table.
        (await s.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM couriers c
            WHERE c.state = 'Offered'
              AND NOT EXISTS (SELECT 1 FROM offers o WHERE o.id = c.current_offer_id AND o.courier_id = c.id AND o.status = 'Pending')
            """)).ShouldBe(0, $"an Offered courier without a matching pending offer {when}");

        (await s.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM couriers c
            WHERE c.state = 'OnDelivery'
              AND NOT EXISTS (SELECT 1 FROM offers o WHERE o.order_id = c.current_order_id AND o.courier_id = c.id AND o.status = 'Accepted')
            """)).ShouldBe(0, $"an OnDelivery courier without a matching accepted offer {when}");
    }

    [Fact]
    public async Task R2_AcceptAndTimeoutAtTheSameInstant_ExactlyOneIsApplied()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var accepted = 0;
        var expired = 0;

        for (var round = 1; round <= Rounds; round++)
        {
            var seeded = await s.SeedPendingOfferAsync();

            // Two servers whose clocks differ by a millisecond around the expiry: to the first the offer is still alive, to
            // the second it is already due. Both conditions are true at once, so only the "status = Pending" part of the
            // conditional UPDATEs can decide who wins. (With one shared clock the time conditions alone would exclude each other.)
            var acceptClock = new FakeTimeProvider(seeded.ExpiresAt - TimeSpan.FromMilliseconds(1));
            var timeoutClock = new FakeTimeProvider(seeded.ExpiresAt);

            await using var acceptScope = s.Scope();
            await using var timeoutScope = s.Scope();
            var accept = new OfferResponseService(acceptScope.ServiceProvider.GetRequiredService<DispatchDbContext>(),
                acceptScope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>(), acceptClock);
            var timeout = new OfferTimeoutProcessor(timeoutScope.ServiceProvider.GetRequiredService<DispatchDbContext>(),
                timeoutScope.ServiceProvider.GetRequiredService<OfferNotifier>(),
                timeoutScope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>(), timeoutClock);

            var gate = new TaskCompletionSource();
            var acceptTask = Task.Run(async () =>
            {
                await gate.Task;
                return await accept.AcceptAsync(seeded.OfferId, seeded.CourierId, Cancel);
            });
            var timeoutTask = Task.Run(async () =>
            {
                await gate.Task;
                return await timeout.ProcessDueAsync(10, Cancel);
            });
            gate.SetResult();
            var acceptResult = await acceptTask;
            var expiredCount = await timeoutTask;

            var acceptedNow = acceptResult.IsSuccess;
            var expiredNow = expiredCount == 1;
            (acceptedNow ^ expiredNow).ShouldBeTrue($"round {round}: accepted={acceptedNow}, expired={expiredNow}; exactly one must win");

            var offer = await s.GetOfferAsync(seeded.OfferId);
            var courier = await s.GetCourierAsync(seeded.CourierId);
            var job = await s.GetJobAsync(seeded.OrderId);
            if (acceptedNow)
            {
                accepted++;
                offer.Status.ShouldBe(OfferStatus.Accepted, $"round {round}");
                courier.State.ShouldBe(CourierState.OnDelivery, $"round {round}");
                job.Status.ShouldBe(DispatchJobStatus.Completed, $"round {round}");
            }
            else
            {
                expired++;
                acceptResult.Error!.Code.ShouldBe("offer-not-pending", $"round {round}");
                offer.Status.ShouldBe(OfferStatus.Expired, $"round {round}");
                courier.State.ShouldBe(CourierState.Available, $"round {round}");
                job.Status.ShouldBe(DispatchJobStatus.Searching, $"round {round}");
            }
        }

        // Not an assertion about who wins (that is up to the engine's scheduling); it shows the test really raced.
        (accepted + expired).ShouldBe(Rounds);
    }

    [Fact]
    public async Task R2_AcceptAndTimeoutAtTheSameInstant_NeverProduceBothAssignmentAndFailureEvents()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        for (var round = 1; round <= Rounds; round++)
        {
            // The last attempt: if the timeout wins the job gives up (DispatchFailed); if the accept wins it is assigned.
            var seeded = await s.SeedPendingOfferAsync(attempt: DispatchScenario.Rules.MaxOfferAttempts);
            var acceptClock = new FakeTimeProvider(seeded.ExpiresAt - TimeSpan.FromMilliseconds(1));
            var timeoutClock = new FakeTimeProvider(seeded.ExpiresAt);

            await using var acceptScope = s.Scope();
            await using var timeoutScope = s.Scope();
            var accept = new OfferResponseService(acceptScope.ServiceProvider.GetRequiredService<DispatchDbContext>(),
                acceptScope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>(), acceptClock);
            var timeout = new OfferTimeoutProcessor(timeoutScope.ServiceProvider.GetRequiredService<DispatchDbContext>(),
                timeoutScope.ServiceProvider.GetRequiredService<OfferNotifier>(),
                timeoutScope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>(), timeoutClock);

            var gate = new TaskCompletionSource();
            var acceptTask = Task.Run(async () =>
            {
                await gate.Task;
                return await accept.AcceptAsync(seeded.OfferId, seeded.CourierId, Cancel);
            });
            var timeoutTask = Task.Run(async () =>
            {
                await gate.Task;
                return await timeout.ProcessDueAsync(10, Cancel);
            });
            gate.SetResult();
            await Task.WhenAll(acceptTask, timeoutTask);
        }

        // Per order: never an assignment and a failure for the same order. Count orders that have both.
        var both = await s.CountAsync($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT a.payload->'data'->>'orderId' AS order_id FROM outbox_messages a WHERE a.type = 'CourierAssigned.v1'
                INTERSECT
                SELECT f.payload->'data'->>'orderId' FROM outbox_messages f WHERE f.type = 'DispatchFailed.v1') t
            """);
        both.ShouldBe(0, "an order was both assigned and reported failed");
    }

    [Fact]
    public async Task R3_TwoTimeoutWorkersAtTheSameTime_ProcessEveryOfferExactlyOnce()
    {
        const int offers = 40;
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        for (var round = 1; round <= 5; round++)
        {
            var seeded = new List<SeededOffer>();
            for (var i = 0; i < offers; i++)
            {
                seeded.Add(await s.SeedPendingOfferAsync());
            }

            s.Time.Advance(DispatchScenario.Rules.OfferTtl); // all of them are due now

            var gate = new TaskCompletionSource();
            var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                var processed = 0;

                // Small batches, so the two workers interleave over many rounds instead of one taking everything at once.
                while (await s.CountAsync($"""SELECT count(*)::int AS "Value" FROM offers WHERE status = 'Pending'""") > 0)
                {
                    await using var scope = s.Scope();
                    processed += await scope.ServiceProvider.GetRequiredService<OfferTimeoutProcessor>().ProcessDueAsync(3, Cancel);
                    await Task.Yield();
                }

                return processed;
            })).ToList();
            gate.SetResult();
            var counts = await Task.WhenAll(workers);

            counts.Sum().ShouldBe(offers, $"round {round}: {string.Join(" + ", counts)}; every offer must be expired by exactly one worker");

            foreach (var offer in seeded)
            {
                (await s.GetOfferAsync(offer.OfferId)).Status.ShouldBe(OfferStatus.Expired);

                // Seeded at version 2 (shift start, offer received); one release makes 3. A second processing would make 4.
                (await s.GetCourierAsync(offer.CourierId)).Version.ShouldBe(3, $"round {round}: courier released more than once");
                (await s.GetJobAsync(offer.OrderId)).OfferAttempts.ShouldBe(1);
            }
        }

        (await s.GetOutboxAsync("CourierStateChanged.v1")).Count.ShouldBe(5 * offers, "one state-changed event per released courier, none duplicated");
    }
}
