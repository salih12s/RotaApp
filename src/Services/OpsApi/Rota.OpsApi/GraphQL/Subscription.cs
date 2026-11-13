using System.Security.Claims;
using System.Threading.Channels;
using HotChocolate.Authorization;
using HotChocolate.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Web.Auth;
using Rota.OpsApi.Infrastructure;
using static Microsoft.AspNetCore.Authorization.AuthorizationServiceExtensions;
using IAuthorizationService = Microsoft.AspNetCore.Authorization.IAuthorizationService;
using System.Runtime.CompilerServices;

namespace Rota.OpsApi.GraphQL;

/// <summary>What the customer sees: where the courier is. No courier id, name or anything else about the courier.</summary>
public sealed record TrackingUpdate(Guid OrderId, double Lat, double Lon, DateTimeOffset RecordedAt);

/// <summary>
/// Live data over WebSocket (docs/05 §5). A subscription is a long-lived request: the server keeps it open and pushes. Two for the dispatcher
/// (positions, order status changes) and one for the customer, who has no account and proves the right to watch one order with a signed tracking link.
/// </summary>
public sealed class Subscription
{
    private const string TrackingUnavailable = "tracking-not-available";

    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    [Subscribe(With = nameof(WatchCouriers))]
    public CourierLocationUpdate CourierLocationUpdated(IReadOnlyList<Guid>? courierIds, [EventMessage] CourierLocationUpdate message) => message;

    public async IAsyncEnumerable<CourierLocationUpdate> WatchCouriers(
        IReadOnlyList<Guid>? courierIds,
        ClaimsPrincipal user,
        [Service] IAuthorizationService authorization,
        [Service] CourierLocationFeed feed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RequireDispatcherAsync(user, authorization);

        // No list = every courier.
        HashSet<Guid>? wanted = courierIds is { Count: > 0 } ? [.. courierIds] : null;
        await foreach (var update in feed.SubscribeAsync(id => wanted is null || wanted.Contains(id), cancellationToken))
        {
            yield return update;
        }
    }

    [Authorize(Policy = RotaPolicies.DispatcherOnly)]
    [Subscribe(With = nameof(WatchOrderStatuses))]
    public OrderStatusChangedEvent OrderStatusChanged([EventMessage] OrderStatusChangedEvent message) => message;

    public async IAsyncEnumerable<OrderStatusChangedEvent> WatchOrderStatuses(
        ClaimsPrincipal user,
        [Service] IAuthorizationService authorization,
        [Service] ITopicEventReceiver receiver,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RequireDispatcherAsync(user, authorization);

        await using var statuses = await receiver.SubscribeAsync<OrderStatusChangedEvent>(OrderStatusNotifier.Topic, cancellationToken);
        await foreach (var change in statuses.ReadEventsAsync().WithCancellation(cancellationToken))
        {
            yield return change;
        }
    }

    /// <summary>
    /// The customer's view of one delivery. Anonymous at the GraphQL level (the tracking link <b>is</b> the credential), so everything is checked here:
    /// signature, expiry and order in the token; then the order must be <c>Assigned</c> or <c>PickedUp</c> (I-9). The stream ends when the order leaves
    /// those states or the link expires, so a delivered order's courier is not followed afterwards.
    /// </summary>
    [AllowAnonymous]
    [Subscribe(With = nameof(WatchOrder))]
    public TrackingUpdate OrderTracking(string trackingToken, [EventMessage] TrackingUpdate message) => message;

    public async IAsyncEnumerable<TrackingUpdate> WatchOrder(
        string trackingToken,
        [Service] TrackingTokenValidator validator,
        [Service] IDbContextFactory<OpsDbContext> factory,
        [Service] CourierLocationFeed feed,
        [Service] ITopicEventReceiver receiver,
        [Service] TimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TrackingGrant grant;
        try
        {
            var validated = await validator.ValidateAsync(trackingToken, cancellationToken);
            if (validated.IsFailure)
            {
                // One answer for forged, expired and wrong-audience tokens: the caller learns nothing about which check failed.
                throw Refuse(validated.Error.Code, validated.Error.Message);
            }

            grant = validated.Value;
        }
        catch (HttpRequestException)
        {
            throw Refuse("dependency-unavailable", "Tracking links cannot be checked right now; try again in a moment.");
        }

        // Listen for status changes first, then read the status: a change that lands between the two cannot be missed.
        await using var statuses = await receiver.SubscribeAsync<OrderStatusChangedEvent>(OrderStatusNotifier.Topic, cancellationToken);

        OrderView? order;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == grant.OrderId, cancellationToken);
        }

        if (order is not { CourierId: { } courierId } || !IsWatchable(order.Status))
        {
            throw Refuse(TrackingUnavailable, "This delivery cannot be followed right now.");
        }

        // `stop` ends the stream when the link expires or the order is no longer watchable; it is also cancelled if the client leaves.
        var remaining = grant.ExpiresAt - time.GetUtcNow();
        using var expiry = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

        // Positions can arrive faster than a client reads; only the newest matter, so the oldest is dropped.
        var positions = Channel.CreateBounded<TrackingUpdate>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        var pumps = new[]
        {
            Pump(positions.Writer, async () =>
            {
                await foreach (var p in feed.SubscribeAsync(id => id == courierId, stop.Token))
                {
                    positions.Writer.TryWrite(new TrackingUpdate(grant.OrderId, p.Lat, p.Lon, p.RecordedAt));
                }
            }),
            Pump(positions.Writer, async () =>
            {
                await foreach (var change in statuses.ReadEventsAsync().WithCancellation(stop.Token))
                {
                    if (change.OrderId == grant.OrderId && !IsWatchable(change.ToStatus))
                    {
                        await stop.CancelAsync();
                    }
                }
            }),
        };

        try
        {
            while (true)
            {
                try
                {
                    if (!await positions.Reader.WaitToReadAsync(stop.Token))
                    {
                        yield break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Expired, delivered or cancelled: a normal end, not an error.
                    yield break;
                }

                while (positions.Reader.TryRead(out var update))
                {
                    yield return update;
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(pumps);
        }
    }

    private static bool IsWatchable(OrderStatus status) => status is OrderStatus.Assigned or OrderStatus.PickedUp;

    /// <summary>Runs one producer; its failure (Redis lost, say) reaches the reader as the error that ends the stream instead of vanishing in a background task.</summary>
    private static async Task Pump(ChannelWriter<TrackingUpdate> writer, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    /// <summary>
    /// <c>[Authorize]</c> on a subscription field is checked when an <b>event</b> is delivered, not when the subscription starts; without this check anyone could
    /// open the dispatcher feeds and sit there. So the subscribe step asks the same policy itself, before anything is registered.
    /// </summary>
    private static async Task RequireDispatcherAsync(ClaimsPrincipal user, IAuthorizationService authorization)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            throw Refuse("AUTH_NOT_AUTHENTICATED", "The current user is not authenticated.");
        }

        if (!(await authorization.AuthorizeAsync(user, RotaPolicies.DispatcherOnly)).Succeeded)
        {
            throw Refuse("AUTH_NOT_AUTHORIZED", "The current user is not authorized to access this resource.");
        }
    }

    private static SubscriptionRefusedException Refuse(string code, string message) => new(code, message);
}
