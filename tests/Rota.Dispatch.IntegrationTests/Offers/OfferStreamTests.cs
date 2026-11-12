using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rota.Contracts.Dispatch.V1;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Infrastructure;
using Rota.TestKit;
using DispatchEvent = Rota.Contracts.Dispatch.V1.OfferEvent;

namespace Rota.Dispatch.IntegrationTests.Offers;

/// <summary>
/// The courier's offer stream (<c>SubscribeOffers</c>) over a real gRPC call and a real Redis. gRPC tests, because stream behaviour
/// (order of events, reconnecting, heartbeat) only shows on a real call. Waiting uses "until it happens, at most N seconds", never a fixed sleep.
/// </summary>
public sealed class OfferStreamTests(DispatchFixture fixture)
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A short heartbeat keeps the polling and heartbeat tests fast; the production value is 15 s.
    private static Dictionary<string, string> FastHeartbeat => new() { ["Dispatch:StreamHeartbeat"] = "00:00:00.300" };

    private sealed class Subscription(AsyncServerStreamingCall<DispatchEvent> call, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async Task<DispatchEvent> NextAsync(CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_patience);
            (await call.ResponseStream.MoveNext(limit.Token)).ShouldBeTrue("the stream ended");
            return call.ResponseStream.Current;
        }

        /// <summary>Next event that is not a heartbeat.</summary>
        public async Task<DispatchEvent> NextRealEventAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var next = await NextAsync(cancellationToken);
                if (next.EventCase != DispatchEvent.EventOneofCase.Heartbeat)
                {
                    return next;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            call.Dispose();
            stop.Dispose();
        }
    }

    private static Subscription Subscribe(DispatchScenario s, Guid courierId, Grpc.Net.Client.GrpcChannel channel)
    {
        var stop = new CancellationTokenSource();
        var client = new CourierOffers.CourierOffersClient(channel);
        var call = client.SubscribeOffers(new SubscribeOffersRequest(), DispatchApiFactory.As(DevUsers.Courier(courierId)), cancellationToken: stop.Token);
        return new Subscription(call, stop);
    }

    [Fact]
    public async Task Subscribe_WithAnOfferAlreadyWaiting_SendsItFirst()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        using var channel = s.Api.CreateGrpcChannel();

        await using var stream = Subscribe(s, seeded.CourierId, channel);
        var first = await stream.NextAsync(Cancel);

        first.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        first.Offer.OfferId.ShouldBe(seeded.OfferId.ToString("D"));
        first.Offer.OrderId.ShouldBe(seeded.OrderId.ToString("D"));
        first.Offer.Pickup.Address.ShouldBe("Pickup Street 1");
        first.Offer.Dropoff.Address.ShouldBe("Dropoff Street 2");
        first.Offer.DistanceM.ShouldBe(500);
        first.Offer.EtaSeconds.ShouldBe(90);
        first.Offer.ExpiresAt.ToDateTimeOffset().ShouldBe(seeded.ExpiresAt);
    }

    [Fact]
    public async Task Subscribe_ThenAnOfferIsCreated_ArrivesWithinOneSecond()
    {
        // F05 PASS 4. The path: commit → Redis push → the stream wakes up → reads PostgreSQL → writes to the courier.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 700)];
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, courierId, channel);

        // Heartbeats are not sent before the (15 s) interval, so give the subscription a moment to be in place: wait until
        // Redis reports a subscriber on the courier's channel, instead of sleeping.
        await WaitForSubscriberAsync(courierId);

        var clock = Stopwatch.StartNew();
        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered);
        var received = await stream.NextRealEventAsync(Cancel);
        clock.Stop();

        received.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        received.Offer.OrderId.ShouldBe(orderId.ToString("D"));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Subscribe_AfterAnOfferWasCreatedWhileDisconnected_DeliversItOnReconnect()
    {
        // F05 PASS 4, second half: the offer lives in PostgreSQL, so a courier whose connection dropped loses nothing.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 700)];
        using var channel = s.Api.CreateGrpcChannel();

        await using (var first = Subscribe(s, courierId, channel))
        {
            await WaitForSubscriberAsync(courierId);
        } // the connection drops

        (await s.OfferAsync(orderId, Cancel)).ShouldBe(OfferingOutcome.Offered); // nobody is listening
        await using var second = Subscribe(s, courierId, channel);
        var received = await second.NextAsync(Cancel);

        received.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        received.Offer.OrderId.ShouldBe(orderId.ToString("D"));
    }

    [Fact]
    public async Task Subscribe_WhenThePushIsLost_FindsTheOfferOnTheNextPoll()
    {
        // The offer is written straight into the database, with no Redis message at all: the heartbeat tick polls PostgreSQL.
        await using var s = await DispatchScenario.StartAsync(fixture, FastHeartbeat, Cancel);
        var courierId = await s.SeedCourierAsync();
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, courierId, channel);
        await WaitForSubscriberAsync(courierId);

        var seeded = await s.SeedPendingOfferForExistingCourierAsync(courierId);
        var received = await stream.NextRealEventAsync(Cancel);

        received.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        received.Offer.OfferId.ShouldBe(seeded.OfferId.ToString("D"));
    }

    [Fact]
    public async Task Subscribe_WhenTheOfferExpires_SendsWithdrawn()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, seeded.CourierId, channel);
        (await stream.NextAsync(Cancel)).EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        await WaitForSubscriberAsync(seeded.CourierId);

        s.Time.Advance(DispatchScenario.Rules.OfferTtl);
        await using (var scope = s.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<DispatchRounds>().ExpireOnceAsync(Cancel);
        }

        var withdrawn = await stream.NextRealEventAsync(Cancel);
        withdrawn.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Withdrawn);
        withdrawn.Withdrawn.OfferId.ShouldBe(seeded.OfferId.ToString("D"));
        withdrawn.Withdrawn.Reason.ShouldBe("expired");
    }

    [Fact]
    public async Task Subscribe_WhenTheOrderIsCancelled_SendsWithdrawnWithTheReason()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var seeded = await s.SeedPendingOfferAsync();
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, seeded.CourierId, channel);
        (await stream.NextAsync(Cancel)).EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
        await WaitForSubscriberAsync(seeded.CourierId);

        await s.OrderStatusChangedAsync(seeded.OrderId, "Cancelled", Cancel);

        var withdrawn = await stream.NextRealEventAsync(Cancel);
        withdrawn.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Withdrawn);
        withdrawn.Withdrawn.OfferId.ShouldBe(seeded.OfferId.ToString("D"));
        withdrawn.Withdrawn.Reason.ShouldBe("order-cancelled");
    }

    [Fact]
    public async Task Subscribe_Idle_SendsHeartbeats()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, FastHeartbeat, Cancel);
        var courierId = await s.SeedCourierAsync();
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, courierId, channel);

        var beat = await stream.NextAsync(Cancel);

        beat.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Heartbeat);
        beat.Heartbeat.ServerTime.ToDateTimeOffset().ShouldBe(s.Time.GetUtcNow());
    }

    [Fact]
    public async Task Subscribe_DoesNotShowAnotherCouriersOffer()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, FastHeartbeat, Cancel);
        await s.SeedPendingOfferAsync();
        var other = await s.SeedCourierAsync();
        using var channel = s.Api.CreateGrpcChannel();
        await using var stream = Subscribe(s, other, channel);

        var first = await stream.NextAsync(Cancel);

        first.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Heartbeat, "nothing is waiting for this courier");
    }

    [Fact]
    public async Task Subscribe_ByTwoStreamsOfTheSameCourier_ClosingOneDoesNotSilenceTheOther()
    {
        // Regression guard for the unsubscribe call: it must remove only its own handler, not every handler on the channel.
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        var courierId = await s.SeedCourierAsync();
        var orderId = await s.SeedJobAsync();
        s.Tracking.State.Couriers = [s.Near(courierId, 700)];
        using var channel = s.Api.CreateGrpcChannel();
        await using var survivor = Subscribe(s, courierId, channel);
        await WaitForSubscriberAsync(courierId);
        var doomed = Subscribe(s, courierId, channel);
        await doomed.NextAsync(Cancel).ContinueWith(_ => { }, TaskScheduler.Default); // it is up (a heartbeat or the first sync) or it failed
        await doomed.DisposeAsync();

        // The unsubscribe of the closed stream runs on the server a moment later. This pause can only make the test pass when
        // it should not (too short to catch the bug); it can never make it fail.
        await Task.Delay(300, Cancel);

        await s.OfferAsync(orderId, Cancel);

        var received = await survivor.NextRealEventAsync(Cancel);
        received.EventCase.ShouldBe(DispatchEvent.EventOneofCase.Offer);
    }

    [Fact]
    public async Task Subscribe_WithoutIdentity_IsUnauthenticated()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        using var channel = s.Api.CreateGrpcChannel();
        var client = new CourierOffers.CourierOffersClient(channel);

        using var call = client.SubscribeOffers(new SubscribeOffersRequest(), cancellationToken: Cancel);
        var failure = await Should.ThrowAsync<RpcException>(() => call.ResponseStream.MoveNext(Cancel));

        failure.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Subscribe_AsMerchant_IsPermissionDenied()
    {
        await using var s = await DispatchScenario.StartAsync(fixture, cancellationToken: Cancel);
        using var channel = s.Api.CreateGrpcChannel();
        var client = new CourierOffers.CourierOffersClient(channel);

        using var call = client.SubscribeOffers(new SubscribeOffersRequest(), DispatchApiFactory.As(DevUsers.Merchant("m-1")), cancellationToken: Cancel);
        var failure = await Should.ThrowAsync<RpcException>(() => call.ResponseStream.MoveNext(Cancel));

        failure.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    // The stream subscribes to Redis when it starts; an offer pushed before that would only be found by the poll. Waiting for
    // a subscriber removes that timing question from the tests that are about the push itself. (The probe message makes the
    // stream re-read the database, which finds nothing new.)
    private async Task WaitForSubscriberAsync(Guid courierId)
    {
        var channel = OfferNotifier.ChannelFor(courierId);
        await Eventually.UntilAsync(
            async () => await fixture.Redis.GetSubscriber().PublishAsync(channel, "probe") > 0,
            _patience, $"a stream is subscribed to {channel}", Cancel);
    }
}
