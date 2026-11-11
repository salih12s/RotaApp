using Rota.BuildingBlocks.Primitives;

namespace Rota.Orders.Api.Domain;

/// <summary>
/// Order aggregate root. State changes only through the methods below, and every status
/// change goes through <see cref="TransitionTo"/> and the <see cref="OrderTransitions"/> table.
/// Time is passed in (<c>now</c>) so the domain never reads the clock.
/// </summary>
public sealed class Order
{
    private readonly List<OrderStatusChange> _history = [];

    private Order(
        Guid id,
        string merchantId,
        GeoPoint pickup,
        GeoPoint dropoff,
        int distanceMeters,
        DeliveryCode deliveryCode,
        DateTimeOffset now)
    {
        Id = id;
        MerchantId = merchantId;
        PickupPoint = pickup;
        DropoffPoint = dropoff;
        DistanceMeters = distanceMeters;
        DeliveryCode = deliveryCode;
        Status = OrderStatus.Created;
        Version = 1;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; }

    public string MerchantId { get; }

    public GeoPoint PickupPoint { get; }

    public GeoPoint DropoffPoint { get; }

    public int DistanceMeters { get; }

    public OrderStatus Status { get; private set; }

    public Guid? AssignedCourierId { get; private set; }

    public DeliveryCode DeliveryCode { get; }

    /// <summary>Incremented on every status change; published as <c>orderVersion</c> in events.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<OrderStatusChange> History => _history;

    public static Result<NewOrder> Create(
        Guid id,
        string merchantId,
        GeoPoint pickup,
        GeoPoint dropoff,
        ServiceAreaPolicy serviceArea,
        DeliveryCodeHasher hasher,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        ArgumentNullException.ThrowIfNull(serviceArea);

        var distance = serviceArea.CheckDistance(pickup, dropoff);
        if (distance.IsFailure)
        {
            return distance.Error;
        }

        var (deliveryCode, plainText) = DeliveryCode.Generate(hasher);
        var order = new Order(id, merchantId, pickup, dropoff, distance.Value, deliveryCode, now);
        order._history.Add(new OrderStatusChange(null, OrderStatus.Created, OrderActor.Merchant(merchantId), null, now));

        return new NewOrder(order, plainText);
    }

    public Result MarkDispatching(OrderActor actor, DateTimeOffset now) =>
        TransitionTo(OrderAction.MarkDispatching, actor, reason: null, now);

    public Result Assign(Guid courierId, OrderActor actor, DateTimeOffset now)
    {
        var result = TransitionTo(OrderAction.Assign, actor, reason: null, now);
        if (result.IsSuccess)
        {
            AssignedCourierId = courierId;
        }

        return result;
    }

    public Result MarkUnassigned(OrderActor actor, DateTimeOffset now) =>
        TransitionTo(OrderAction.MarkUnassigned, actor, reason: null, now);

    public Result Cancel(OrderActor actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderAction.Cancel, actor, reason, now);

    public Result PickUp(OrderActor actor, DateTimeOffset now) =>
        TransitionTo(OrderAction.PickUp, actor, reason: null, now);

    /// <summary>
    /// Delivers with the customer's code. A wrong code changes state (attempt counter) even though
    /// it fails, so the caller must persist the order on failure too.
    /// </summary>
    public Result Deliver(string code, DeliveryCodeHasher hasher, OrderActor actor, DateTimeOffset now)
    {
        // Status first: a code is meaningless before pickup and must not burn attempts.
        if (!OrderTransitions.TryGetTarget(Status, OrderAction.Deliver, out _))
        {
            return OrderErrors.InvalidTransition(Status, OrderAction.Deliver);
        }

        var verification = DeliveryCode.Verify(code, hasher);
        if (verification.IsFailure)
        {
            UpdatedAt = now;
            return verification;
        }

        return TransitionTo(OrderAction.Deliver, actor, reason: null, now);
    }

    public Result FailDelivery(OrderActor actor, string? reason, DateTimeOffset now) =>
        TransitionTo(OrderAction.FailDelivery, actor, reason, now);

    public Result MarkReturned(OrderActor actor, DateTimeOffset now) =>
        TransitionTo(OrderAction.MarkReturned, actor, reason: null, now);

    /// <summary>Dispatcher unlocks a locked code. Only meaningful while the parcel is out for delivery.</summary>
    public Result UnlockDeliveryCode(DateTimeOffset now)
    {
        if (Status != OrderStatus.PickedUp)
        {
            return OrderErrors.UnlockNotAllowed(Status);
        }

        // Idempotent: unlocking an unlocked code is a no-op success (the request may be retried).
        DeliveryCode.Unlock();
        UpdatedAt = now;
        return Result.Success();
    }

    private Result TransitionTo(OrderAction action, OrderActor actor, string? reason, DateTimeOffset now)
    {
        if (!OrderTransitions.TryGetTarget(Status, action, out var target))
        {
            return OrderErrors.InvalidTransition(Status, action);
        }

        _history.Add(new OrderStatusChange(Status, target, actor, reason, now));
        Status = target;
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }
}

/// <summary>A freshly created order plus its plain-text delivery code (returned to the merchant once).</summary>
public sealed record NewOrder(Order Order, string DeliveryCode);
