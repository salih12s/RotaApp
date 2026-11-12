using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Rota.Contracts.Dispatch.V1;

namespace Rota.Sim.CourierFleet;

/// <summary>
/// The "offers" half of a pretend courier (simulator v2): start a shift, keep the offer stream open, and answer each offer
/// the way <see cref="OfferBehavior"/> says (accept, reject, or stay silent). A dropped stream is reopened: the waiting offer
/// is sent again by Dispatch. Simulator v3: a courier who accepted also does the job: picks the parcel up, then (following
/// <see cref="DeliveryBehavior"/>) delivers with the customer's code (sometimes mistyped first), or fails and returns it.
/// Without an Orders address and a code file the courier stops after accepting, as in v2.
/// </summary>
public sealed class OfferResponder(FleetOptions options, FleetStats stats, Rota.Sim.Common.SimCredentials credentials, HttpClient? ordersHttp, CodeBook? codes)
{
    private static readonly TimeSpan _retryAfterError = TimeSpan.FromSeconds(1);

    public async Task RunCourierAsync(CourierOffers.CourierOffersClient offers, HttpClient dispatchHttp, int index, CancellationToken stop)
    {
        var random = new Random(HashCode.Combine(options.Seed, index, 104_729));
        var devUser = $"courier:{options.CourierId(index):D}";

        await StartShiftAsync(dispatchHttp, devUser, stop);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var call = offers.SubscribeOffers(new SubscribeOffersRequest(), await credentials.MetadataAsync(devUser), cancellationToken: stop);
                await foreach (var message in call.ResponseStream.ReadAllAsync(stop))
                {
                    switch (message.EventCase)
                    {
                        case OfferEvent.EventOneofCase.Offer:
                            stats.OfferSeen();

                            // Decided here, in the stream loop, because Random is not thread-safe; only the waiting and the call run apart.
                            var decision = options.OfferBehavior.Decide(random.NextDouble());
                            var thinkTime = options.OfferBehavior.ThinkTime(random.NextDouble());
                            var job = new DeliveryJob(
                                Guid.Parse(message.Offer.OrderId),
                                options.DeliveryBehavior.Decide(random.NextDouble()),
                                TimeSpan.FromSeconds(random.NextDouble() * options.TravelSeconds),
                                TimeSpan.FromSeconds(random.NextDouble() * options.TravelSeconds));
                            _ = RespondAsync(dispatchHttp, devUser, message.Offer.OfferId, decision, thinkTime, job, stop);
                            break;

                        case OfferEvent.EventOneofCase.Withdrawn:
                            stats.OfferWithdrawn();
                            break;
                    }
                }
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
            {
                // Stopping: cancelling a stream that is being read surfaces as a Cancelled status, not only as an OperationCanceledException.
            }
            catch (RpcException)
            {
                stats.OfferStreamFailed();
                await DelayAsync(_retryAfterError, stop);
            }
        }
    }

    private async Task StartShiftAsync(HttpClient http, string devUser, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/couriers/me/shift/start");
        await credentials.SignAsync(request, devUser, stop);
        try
        {
            // 200 = started; 409 = already on shift from an earlier run. Both mean "available".
            using var response = await http.SendAsync(request, stop);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                await Console.Error.WriteLineAsync($"Shift start of {devUser} was answered {(int)response.StatusCode}; this courier will get no offers.");
            }
        }
        catch (HttpRequestException ex)
        {
            await Console.Error.WriteLineAsync($"Shift start of {devUser} failed ({ex.Message}); is Dispatch running at {http.BaseAddress}?");
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RespondAsync(HttpClient http, string devUser, string offerId, OfferDecision decision, TimeSpan thinkTime, DeliveryJob job, CancellationToken stop)
    {
        if (decision == OfferDecision.Ignore)
        {
            stats.OfferIgnored();
            return;
        }

        await DelayAsync(thinkTime, stop);
        if (stop.IsCancellationRequested)
        {
            return;
        }

        var action = decision == OfferDecision.Accept ? "accept" : "reject";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/offers/{offerId}/{action}");
        await credentials.SignAsync(request, devUser, stop);
        try
        {
            using var response = await http.SendAsync(request, stop);
            if (response.IsSuccessStatusCode)
            {
                stats.OfferAnswered(decision);
                if (decision == OfferDecision.Accept && ordersHttp is not null && codes is not null)
                {
                    await DeliverAsync(devUser, job, stop);
                }
            }
            else if (response.StatusCode == HttpStatusCode.Conflict)
            {
                stats.OfferAnswerRefused(); // too late: the offer had expired or the order was cancelled
            }
            else
            {
                stats.OfferAnswerFailed();
            }
        }
        catch (HttpRequestException)
        {
            stats.OfferAnswerFailed();
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>What one courier decided to do with one order, drawn before any waiting starts (Random is not thread-safe).</summary>
    private sealed record DeliveryJob(Guid OrderId, DeliveryPlan Plan, TimeSpan ToPickup, TimeSpan ToCustomer);

    private async Task DeliverAsync(string devUser, DeliveryJob job, CancellationToken stop)
    {
        await DelayAsync(job.ToPickup, stop);
        if (!await PickUpAsync(job.OrderId, devUser, stop))
        {
            return;
        }

        stats.ParcelPickedUp();
        await DelayAsync(job.ToCustomer, stop);

        if (job.Plan == DeliveryPlan.FailAndReturn)
        {
            if (await PostAsync(job.OrderId, "delivery-failed", JsonContent.Create(new { reason = "customer not at home (simulated)" }), devUser, stop))
            {
                stats.DeliveryFailed();
                await DelayAsync(job.ToPickup, stop); // the ride back
                if (await PostAsync(job.OrderId, "returned", content: null, devUser, stop))
                {
                    stats.ParcelReturned();
                }
            }

            return;
        }

        // The customer's code reached us through the code file; if it never shows up the parcel stays out (visible in SQL).
        var code = await codes!.WaitForAsync(job.OrderId, TimeSpan.FromSeconds(30), stop);
        if (code is null)
        {
            stats.CodeMissing();
            return;
        }

        if (job.Plan == DeliveryPlan.MistypeThenDeliver)
        {
            // Expected answer: 400 invalid-delivery-code, which is not an error of the simulator.
            using var wrong = await SendAsync(job.OrderId, "deliver", JsonContent.Create(new { code = DeliveryBehavior.Mistype(code) }), devUser, stop);
            if (wrong?.StatusCode == HttpStatusCode.BadRequest)
            {
                stats.WrongCodeRejected();
            }
            else
            {
                stats.DeliveryCallFailed();
            }
        }

        if (await PostAsync(job.OrderId, "deliver", JsonContent.Create(new { code }), devUser, stop))
        {
            stats.ParcelDelivered();
        }
    }

    /// <summary>
    /// Accepting an offer is answered by Dispatch at once, but Orders learns "this courier has the order" from an event a moment later
    /// (docs/05 §3). Until then the order is not visible to the courier and <c>pickup</c> answers 404. A real app waits a little and asks
    /// again, so the simulator does too: only 404 is retried, and only for a few seconds. Any other answer is final.
    /// </summary>
    private async Task<bool> PickUpAsync(Guid orderId, string devUser, CancellationToken stop)
    {
        for (var attempt = 0; attempt < 20 && !stop.IsCancellationRequested; attempt++)
        {
            using var response = await SendAsync(orderId, "pickup", content: null, devUser, stop);
            if (response is { IsSuccessStatusCode: true })
            {
                return true;
            }

            if (response?.StatusCode != HttpStatusCode.NotFound)
            {
                break;
            }

            stats.PickupRetried();
            await DelayAsync(TimeSpan.FromMilliseconds(500), stop);
        }

        stats.DeliveryCallFailed();
        return false;
    }

    private async Task<bool> PostAsync(Guid orderId, string action, HttpContent? content, string devUser, CancellationToken stop)
    {
        using var response = await SendAsync(orderId, action, content, devUser, stop);
        if (response is { IsSuccessStatusCode: true })
        {
            return true;
        }

        stats.DeliveryCallFailed();
        return false;
    }

    private async Task<HttpResponseMessage?> SendAsync(Guid orderId, string action, HttpContent? content, string devUser, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/{action}") { Content = content };
        await credentials.SignAsync(request, devUser, stop);
        try
        {
            return await ordersHttp!.SendAsync(request, stop);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // stopping
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the caller's loop condition ends the courier.
        }
    }
}
