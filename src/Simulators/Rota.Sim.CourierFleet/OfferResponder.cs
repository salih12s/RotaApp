using System.Net;
using Grpc.Core;
using Rota.Contracts.Dispatch.V1;

namespace Rota.Sim.CourierFleet;

/// <summary>
/// The "offers" half of a pretend courier (simulator v2): start a shift, keep the offer stream open, and answer each offer
/// the way <see cref="OfferBehavior"/> says (accept, reject, or stay silent). A dropped stream is reopened: the waiting offer
/// is sent again by Dispatch. A courier who accepted stays on that delivery; delivering is F06's job, not this simulator's.
/// </summary>
public sealed class OfferResponder(FleetOptions options, FleetStats stats)
{
    private static readonly TimeSpan _retryAfterError = TimeSpan.FromSeconds(1);

    public async Task RunCourierAsync(CourierOffers.CourierOffersClient offers, HttpClient dispatchHttp, int index, CancellationToken stop)
    {
        var random = new Random(HashCode.Combine(options.Seed, index, 104_729));
        var devUser = $"courier:{FleetRunner.CourierIdentity(options.Seed, index):D}";
        var headers = new Metadata { { "x-dev-user", devUser } };

        await StartShiftAsync(dispatchHttp, devUser, stop);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var call = offers.SubscribeOffers(new SubscribeOffersRequest(), headers, cancellationToken: stop);
                await foreach (var message in call.ResponseStream.ReadAllAsync(stop))
                {
                    switch (message.EventCase)
                    {
                        case OfferEvent.EventOneofCase.Offer:
                            stats.OfferSeen();

                            // Decided here, in the stream loop, because Random is not thread-safe; only the waiting and the call run apart.
                            var decision = options.OfferBehavior.Decide(random.NextDouble());
                            var thinkTime = options.OfferBehavior.ThinkTime(random.NextDouble());
                            _ = RespondAsync(dispatchHttp, devUser, message.Offer.OfferId, decision, thinkTime, stop);
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

    private static async Task StartShiftAsync(HttpClient http, string devUser, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/couriers/me/shift/start");
        request.Headers.Add("X-Dev-User", devUser);
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

    private async Task RespondAsync(HttpClient http, string devUser, string offerId, OfferDecision decision, TimeSpan thinkTime, CancellationToken stop)
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
        request.Headers.Add("X-Dev-User", devUser);
        try
        {
            using var response = await http.SendAsync(request, stop);
            if (response.IsSuccessStatusCode)
            {
                stats.OfferAnswered(decision);
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
