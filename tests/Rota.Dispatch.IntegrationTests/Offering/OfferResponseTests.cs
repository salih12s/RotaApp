using System.Net;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests.Offering;

/// <summary>The courier's side over HTTP: accept, reject, "what is waiting for me?". API tests: status codes, ownership, state.</summary>
public sealed class OfferResponseTests(DispatchFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accept_PendingOffer_AssignsTheCourierAndReportsCourierAssigned()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Accepted);
        var courier = await s.GetCourierAsync(seeded.CourierId);
        courier.State.ShouldBe(CourierState.OnDelivery);
        courier.CurrentOrderId.ShouldBe(seeded.OrderId);
        courier.CurrentOfferId.ShouldBeNull();
        (await s.GetJobAsync(seeded.OrderId)).Status.ShouldBe(DispatchJobStatus.Completed);

        var assigned = DispatchScenario.DataOf((await s.GetOutboxAsync("CourierAssigned.v1")).ShouldHaveSingleItem());
        assigned.GetProperty("orderId").GetGuid().ShouldBe(seeded.OrderId);
        assigned.GetProperty("courierId").GetGuid().ShouldBe(seeded.CourierId);
        assigned.GetProperty("offerId").GetGuid().ShouldBe(seeded.OfferId);
        assigned.GetProperty("assignmentType").GetString().ShouldBe("Auto");
        assigned.GetProperty("etaSeconds").GetInt32().ShouldBe(90);
    }

    [Fact]
    public async Task Accept_ByAnotherCourier_Returns404AndLeavesTheOfferPending()
    {
        // Not 403: "this offer exists but is not yours" must look exactly like "no such offer" (docs/06 §2).
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        var stranger = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(stranger))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await s.GetOfferAsync(seeded.OfferId)).Status.ShouldBe(OfferStatus.Pending);
        (await s.GetCourierAsync(seeded.CourierId)).State.ShouldBe(CourierState.Offered);
    }

    [Fact]
    public async Task Accept_UnknownOffer_Returns404()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()))
            .PostAsync($"/api/v1/offers/{Guid.CreateVersion7()}/accept", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Accept_WithoutIdentity_Returns401()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);

        var response = await s.Api.CreateClient().PostAsync($"/api/v1/offers/{Guid.CreateVersion7()}/accept", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accept_AsMerchant_Returns403()
    {
        // The role is checked before ownership: a merchant is the wrong kind of caller whatever the offer is.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Accept_AfterTheOfferExpired_Returns409EvenBeforeTheTimeoutJobRan()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        s.Time.Advance(DispatchScenario.Rules.OfferTtl); // expired, but nobody has closed it yet

        var response = await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer-not-pending");
        (await s.GetCourierAsync(seeded.CourierId)).State.ShouldBe(CourierState.Offered, "nothing was changed by the refused accept");
        (await s.GetOutboxAsync("CourierAssigned.v1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Accept_Twice_SecondCallIs409AndDoesNotAssignAgain()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        var courier = s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId));

        (await courier.PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await courier.PostAsync($"/api/v1/offers/{seeded.OfferId}/accept", content: null, Cancel);

        await second.ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer-not-pending");
        (await s.GetOutboxAsync("CourierAssigned.v1")).Count.ShouldBe(1, "a retried request must not announce a second assignment");
    }

    [Fact]
    public async Task Reject_PendingOffer_FreesTheCourierAndSendsTheJobBackToSearching()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/reject", content: null, Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var offer = await s.GetOfferAsync(seeded.OfferId);
        offer.Status.ShouldBe(OfferStatus.Rejected);
        offer.RespondedAt.ShouldNotBeNull();
        var courier = await s.GetCourierAsync(seeded.CourierId);
        courier.State.ShouldBe(CourierState.Available);
        courier.CurrentOfferId.ShouldBeNull();
        courier.CurrentOrderId.ShouldBeNull();
        var job = await s.GetJobAsync(seeded.OrderId);
        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.OfferAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Reject_OnTheLastAttempt_MarksTheJobUnassignedAndReportsMaxAttemptsReached()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync(attempt: DispatchScenario.Rules.MaxOfferAttempts);

        await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId))
            .PostAsync($"/api/v1/offers/{seeded.OfferId}/reject", content: null, Cancel);

        (await s.GetJobAsync(seeded.OrderId)).Status.ShouldBe(DispatchJobStatus.Unassigned);
        var failed = DispatchScenario.DataOf((await s.GetOutboxAsync("DispatchFailed.v1")).ShouldHaveSingleItem());
        failed.GetProperty("orderId").GetGuid().ShouldBe(seeded.OrderId);
        failed.GetProperty("reason").GetString().ShouldBe("MaxAttemptsReached");
    }

    [Fact]
    public async Task MyOffers_ReturnsThePendingOfferWithBothAddresses()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(seeded.CourierId)).GetAsync("/api/v1/couriers/me/offers", Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var offer = (await response.ReadJsonAsync()).EnumerateArray().ShouldHaveSingleItem();
        offer.GetProperty("id").GetGuid().ShouldBe(seeded.OfferId);
        offer.GetProperty("orderId").GetGuid().ShouldBe(seeded.OrderId);
        offer.GetProperty("pickup").GetProperty("address").GetString().ShouldBe("Pickup Street 1");
        offer.GetProperty("dropoff").GetProperty("address").GetString().ShouldBe("Dropoff Street 2");
        offer.GetProperty("distanceMeters").GetInt32().ShouldBe(500);
        offer.GetProperty("etaSeconds").GetInt32().ShouldBe(90);
    }

    [Fact]
    public async Task MyOffers_WhenNothingIsWaiting_ReturnsEmptyList()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(courierId)).GetAsync("/api/v1/couriers/me/offers", Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task MyOffers_DoesNotShowAnotherCouriersOffer()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        await s.SeedPendingOfferAsync();
        var other = await s.SeedCourierAsync();

        var response = await s.Api.CreateClientAs(DevUsers.Courier(other)).GetAsync("/api/v1/couriers/me/offers", Cancel);

        (await response.ReadJsonAsync()).GetArrayLength().ShouldBe(0);
    }
}
