using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Rota.Contracts.Dispatch.V1;
using Rota.Contracts.Tracking.V1;

namespace Rota.Sim.CourierFleet;

/// <summary>
/// Runs N pretend couriers, each with its own position stream to Tracking, until told to stop.
/// A courier sends a point every interval; now and then it drops off the network, keeps moving while
/// offline, and delivers the points it collected when it is back (just as a phone would).
/// </summary>
public sealed class FleetRunner(FleetOptions options, FleetStats stats, TimeProvider time)
{
    private static readonly TimeSpan _retryAfterError = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken stop)
    {
        // Many streams over one connection hit HTTP/2's per-connection stream limit (100 in Kestrel);
        // letting the client open further connections keeps hundreds of couriers possible.
        using var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        };
        using var channel = GrpcChannel.ForAddress(options.Url, new GrpcChannelOptions { HttpHandler = handler });
        var ingest = new CourierLocationIngest.CourierLocationIngestClient(channel);

        var tasks = Enumerable.Range(0, options.Couriers).Select(i => RunCourierAsync(ingest, i, stop)).ToList();
        if (options.ProbeNearby)
        {
            tasks.Add(ProbeNearbyAsync(new CourierLocationQuery.CourierLocationQueryClient(channel), stop));
        }

        // Simulator v2: the same couriers also work offers. Dispatch is another service on another port, so it gets its own channel
        // and its own handlers: a gRPC channel takes over the handler it is given, which can then not serve another channel or a plain HttpClient.
        using var dispatchHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        };
        using var dispatchChannel = options.Offers ? GrpcChannel.ForAddress(options.DispatchGrpcUrl, new GrpcChannelOptions { HttpHandler = dispatchHandler }) : null;
        using var ordersHttp = options.Offers && options.CodesFile is not null ? new HttpClient { BaseAddress = options.OrdersUrl } : null;
        using var dispatchHttp = options.Offers ? new HttpClient { BaseAddress = options.DispatchHttpUrl } : null;
        if (dispatchChannel is not null && dispatchHttp is not null)
        {
            var offers = new CourierOffers.CourierOffersClient(dispatchChannel);
            var responder = new OfferResponder(options, stats, ordersHttp, options.CodesFile is null ? null : new CodeBook(options.CodesFile));
            tasks.AddRange(Enumerable.Range(0, options.Couriers).Select(i => responder.RunCourierAsync(offers, dispatchHttp, i, stop)));
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunCourierAsync(CourierLocationIngest.CourierLocationIngestClient ingest, int index, CancellationToken stop)
    {
        var random = new Random(HashCode.Combine(options.Seed, index));
        var walker = new RouteWalker(random);
        var interval = TimeSpan.FromSeconds(options.IntervalSeconds);
        var identity = CourierIdentity(options.Seed, index);
        var headers = new Metadata { { "x-dev-user", $"courier:{identity:D}" } };

        // A real phone keeps its counter across restarts. The simulator derives it from the clock, so a
        // second run continues above the first one (otherwise the server, rightly, calls every point old).
        var seq = time.GetUtcNow().ToUnixTimeSeconds() * 1000;
        var offlineBuffer = new Queue<LocationPoint>();

        // Spread the couriers over one interval instead of all sending in the same instant.
        await DelayAsync(TimeSpan.FromSeconds(random.NextDouble() * interval.TotalSeconds), stop);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Not cancelled on stop: a stream is ended by completing it, so the server sees a clean close.
                using var call = ingest.StreamLocations(headers, cancellationToken: CancellationToken.None);
                var wentOffline = false;
                LocationPoint? held = null;

                // Back online: deliver what was collected, oldest first.
                while (offlineBuffer.TryDequeue(out var old))
                {
                    await SendAsync(call, old);
                }

                while (!stop.IsCancellationRequested && !wentOffline)
                {
                    var point = NextPoint(walker, random, ref seq, interval);
                    if (held is null && random.NextDouble() < options.ReorderRate)
                    {
                        held = point; // delivered after the next one
                    }
                    else
                    {
                        await SendAsync(call, point);
                        if (held is not null)
                        {
                            await SendAsync(call, held);
                            held = null;
                        }
                    }

                    await DelayAsync(interval, stop);
                    wentOffline = random.NextDouble() < options.DisconnectRate;
                }

                if (held is not null)
                {
                    await SendAsync(call, held);
                }

                await call.RequestStream.CompleteAsync();
                var result = await call.ResponseAsync;
                stats.StreamEnded(result.Accepted, result.OutOfOrder);

                if (wentOffline)
                {
                    stats.WentOffline();
                    var offlineFor = TimeSpan.FromSeconds(3 + (random.NextDouble() * 7));
                    var until = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetElapsedTime(until) < offlineFor && !stop.IsCancellationRequested)
                    {
                        offlineBuffer.Enqueue(NextPoint(walker, random, ref seq, interval)); // still moving, just not sending
                        await DelayAsync(interval, stop);
                    }
                }
            }
            catch (RpcException)
            {
                stats.StreamFailed();
                await DelayAsync(_retryAfterError, stop);
            }
        }
    }

    private LocationPoint NextPoint(RouteWalker walker, Random random, ref long seq, TimeSpan interval)
    {
        var (lat, lon) = walker.Step(interval);
        return new LocationPoint
        {
            Seq = ++seq,
            Lat = lat,
            Lon = lon,
            AccuracyM = (float)(5 + (random.NextDouble() * 10)),
            RecordedAt = Timestamp.FromDateTimeOffset(time.GetUtcNow()),
        };
    }

    private async Task SendAsync(AsyncClientStreamingCall<LocationPoint, StreamLocationsResult> call, LocationPoint point)
    {
        await call.RequestStream.WriteAsync(point);
        stats.PointSent();
    }

    private async Task ProbeNearbyAsync(CourierLocationQuery.CourierLocationQueryClient query, CancellationToken stop)
    {
        var headers = new Metadata { { "x-dev-user", "service:courier-fleet-sim" } };
        var request = new GetNearbyCouriersRequest { Lat = 41.01, Lon = 28.98, RadiusM = 5_000, Limit = 20 };

        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await query.GetNearbyCouriersAsync(request, headers, cancellationToken: CancellationToken.None);
                stats.ProbeAnswered(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (RpcException)
            {
                stats.ProbeFailed();
            }

            await DelayAsync(TimeSpan.FromMilliseconds(100), stop);
        }
    }

    /// <summary>The same seed and index always give the same courier, so a run can be repeated.</summary>
    public static Guid CourierIdentity(int seed, int index)
    {
        var bytes = new byte[16];
        new Random(HashCode.Combine(seed, index, 7919)).NextBytes(bytes);
        return new Guid(bytes);
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
