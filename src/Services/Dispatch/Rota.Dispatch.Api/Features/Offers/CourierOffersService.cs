using System.Diagnostics;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Web.Auth;
using Rota.Contracts.Dispatch.V1;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Features.Offering;
using Rota.Dispatch.Api.Infrastructure;
using StackExchange.Redis;

namespace Rota.Dispatch.Api.Features.Offers;

/// <summary>
/// The courier's offer stream (<c>SubscribeOffers</c>, server-streaming). The courier keeps the call open and Dispatch writes
/// events into it: a waiting offer first, then new offers, then "withdrawn", plus a heartbeat every 15 s.
/// <para>
/// Redis Pub/Sub only <i>wakes</i> the stream (any Dispatch instance may have created the offer). What to show always comes
/// from PostgreSQL in <see cref="SyncAsync"/>, which compares "pending offers now" with "offers already shown". So a lost,
/// late or duplicated push cannot show a wrong offer, and with Redis down the stream still catches up on its next poll.
/// </para>
/// </summary>
[Authorize(Policy = RotaPolicies.CourierOnly)]
public sealed partial class CourierOffersService(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    IOptions<DispatchOptions> options,
    TimeProvider time,
    ILogger<CourierOffersService> logger)
    : CourierOffers.CourierOffersBase
{
    // Without a working subscription the stream polls the database this often instead of waiting for a push.
    private static readonly TimeSpan _pollWithoutRedis = TimeSpan.FromSeconds(1);

    public override async Task SubscribeOffers(SubscribeOffersRequest request, IServerStreamWriter<OfferEvent> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var courierId = context.GetHttpContext().User.GetCourierId()
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "Only couriers may subscribe to offers."));
        var cancellationToken = context.CancellationToken;
        var heartbeat = options.Value.StreamHeartbeat;

        // A "something changed" signal is enough; a full channel already has one waiting, so extra signals can be dropped.
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var channel = OfferNotifier.ChannelFor(courierId);

        // One delegate instance: Unsubscribe must remove exactly this stream's handler, not another stream of the same courier.
        Action<RedisChannel, RedisValue> onPush = (_, _) => wake.Writer.TryWrite(true);

        var subscribed = await TrySubscribeAsync(channel, onPush);
        var shown = new HashSet<Guid>();
        var sinceLastWrite = Stopwatch.StartNew();

        try
        {
            // Subscribed first, synced second: an offer created in between triggers a push that is already queued.
            await SyncAsync(courierId, shown, responseStream, cancellationToken);
            sinceLastWrite.Restart();

            while (!cancellationToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(subscribed ? heartbeat : MinOf(heartbeat, _pollWithoutRedis));

                try
                {
                    await wake.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The wait ran out: time for the poll (and, if due, the heartbeat).
                    if (!subscribed)
                    {
                        subscribed = await TrySubscribeAsync(channel, onPush);
                    }
                }

                await SyncAsync(courierId, shown, responseStream, cancellationToken);

                if (sinceLastWrite.Elapsed >= heartbeat)
                {
                    await WriteAsync(responseStream, new OfferEvent { Heartbeat = new Heartbeat { ServerTime = Timestamp.FromDateTimeOffset(time.GetUtcNow()) } }, cancellationToken);
                    sinceLastWrite.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The courier disconnected; nothing to report.
        }
        finally
        {
            await TryUnsubscribeAsync(channel, onPush);
        }
    }

    private static TimeSpan MinOf(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    /// <summary>Shows pending offers not yet shown; withdraws shown offers that were cancelled or expired.</summary>
    private async Task SyncAsync(Guid courierId, HashSet<Guid> shown, IServerStreamWriter<OfferEvent> stream, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        var pending = await db.OfferViews(o => o.CourierId == courierId && o.Status == OfferStatus.Pending).ToListAsync(cancellationToken);

        foreach (var view in pending.Where(v => !shown.Contains(v.OfferId)))
        {
            await WriteAsync(stream, new OfferEvent { Offer = ToMessage(view) }, cancellationToken);
            shown.Add(view.OfferId);
        }

        var pendingIds = pending.Select(v => v.OfferId).ToHashSet();
        foreach (var gone in shown.Where(id => !pendingIds.Contains(id)).ToList())
        {
            var status = await db.Offers.AsNoTracking().Where(o => o.Id == gone).Select(o => o.Status).SingleAsync(cancellationToken);

            // Accepted/Rejected are the courier's own answers; they do not need telling. The others happened to them.
            if (status is OfferStatus.Cancelled or OfferStatus.Expired)
            {
                var reason = status == OfferStatus.Cancelled ? "order-cancelled" : "expired";
                await WriteAsync(stream, new OfferEvent { Withdrawn = new OfferWithdrawn { OfferId = gone.ToString("D"), Reason = reason } }, cancellationToken);
            }

            shown.Remove(gone);
        }
    }

    private static Task WriteAsync(IServerStreamWriter<OfferEvent> stream, OfferEvent message, CancellationToken cancellationToken) =>
        stream.WriteAsync(message, cancellationToken);

    private static Rota.Contracts.Dispatch.V1.Offer ToMessage(OfferView view) => new()
    {
        OfferId = view.OfferId.ToString("D"),
        OrderId = view.OrderId.ToString("D"),
        Pickup = ToMessage(view.Pickup),
        Dropoff = ToMessage(view.Dropoff),
        DistanceM = view.DistanceMeters,
        EtaSeconds = view.EtaSeconds,
        ExpiresAt = Timestamp.FromDateTimeOffset(view.ExpiresAt),
    };

    private static GeoPoint ToMessage(JobPlace place) => new() { Lat = place.Lat, Lon = place.Lon, Address = place.Address };

    private async Task<bool> TrySubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> onPush)
    {
        try
        {
            await redis.GetSubscriber().SubscribeAsync(channel, onPush);
            return true;
        }
        catch (RedisException ex)
        {
            LogSubscribeFailed(logger, ex);
            return false;
        }
    }

    private async Task TryUnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> onPush)
    {
        try
        {
            await redis.GetSubscriber().UnsubscribeAsync(channel, onPush);
        }
        catch (RedisException)
        {
            // Redis is gone; its subscription went with it.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis subscription for an offer stream failed; polling PostgreSQL instead until Redis is back")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception);
}
