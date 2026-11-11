using System.Text;
using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Primitives;
using Rota.Orders.Api.Domain;

namespace Rota.TestKit.Orders;

/// <summary>Test-only HMAC key and coordinates for the Orders domain.</summary>
public static class OrderTestData
{
    public static DeliveryCodeHasher Hasher { get; } =
        new(Encoding.ASCII.GetBytes("rota-test-only-delivery-code-key"));

    // Kadıköy pier → Moda: about 1 km, well inside the service area.
    public static GeoPoint Kadikoy { get; } = GeoPoint.Create(40.9906, 29.0237).Value;

    public static GeoPoint Moda { get; } = GeoPoint.Create(40.9819, 29.0254).Value;
}

/// <summary>An order built for a test, with the inputs a test needs to act on it.</summary>
public sealed record BuiltOrder(Order Order, string DeliveryCode, Guid CourierId);

/// <summary>
/// Builds an <see cref="Order"/> in any status by walking the real transitions (no reflection, no setters),
/// so a test can never start from a state the domain could not reach. Each step advances the clock by 1 minute.
/// </summary>
public sealed class OrderBuilder
{
    private static readonly TimeSpan _step = TimeSpan.FromMinutes(1);

    private OrderStatus _status = OrderStatus.Created;
    private string _merchantId = $"merchant-{Guid.CreateVersion7()}";
    private Guid _courierId = Guid.CreateVersion7();
    private FakeTimeProvider _time = TestClock.Create();

    public OrderBuilder InStatus(OrderStatus status)
    {
        _status = status;
        return this;
    }

    public OrderBuilder ForMerchant(string merchantId)
    {
        _merchantId = merchantId;
        return this;
    }

    public OrderBuilder WithCourier(Guid courierId)
    {
        _courierId = courierId;
        return this;
    }

    public OrderBuilder WithClock(FakeTimeProvider time)
    {
        _time = time;
        return this;
    }

    public BuiltOrder Build()
    {
        var created = Order.Create(
            Guid.CreateVersion7(), _merchantId, OrderTestData.Kadikoy, OrderTestData.Moda,
            ServiceAreaPolicy.Default, OrderTestData.Hasher, _time.GetUtcNow());
        var (order, code) = (created.Value.Order, created.Value.DeliveryCode);

        foreach (var step in PathTo(_status))
        {
            _time.Advance(_step);
            var result = step(order, code);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"OrderBuilder could not reach {_status}: {result.Error.Code} ({result.Error.Message})");
            }
        }

        return new BuiltOrder(order, code, _courierId);
    }

    private List<Func<Order, string, Result>> PathTo(OrderStatus status)
    {
        var now = () => _time.GetUtcNow();
        var courier = OrderActor.Courier(_courierId);

        Result Dispatch(Order o, string _) => o.MarkDispatching(OrderActor.System, now());
        Result Unassign(Order o, string _) => o.MarkUnassigned(OrderActor.System, now());
        Result Assign(Order o, string _) => o.Assign(_courierId, OrderActor.System, now());
        Result PickUp(Order o, string _) => o.PickUp(courier, now());
        Result Deliver(Order o, string c) => o.Deliver(c, OrderTestData.Hasher, courier, now());
        Result Fail(Order o, string _) => o.FailDelivery(courier, "customer absent", now());
        Result Return(Order o, string _) => o.MarkReturned(courier, now());
        Result Cancel(Order o, string _) => o.Cancel(OrderActor.Merchant(_merchantId), "test", now());

        return status switch
        {
            OrderStatus.Created => [],
            OrderStatus.Dispatching => [Dispatch],
            OrderStatus.Unassigned => [Dispatch, Unassign],
            OrderStatus.Assigned => [Dispatch, Assign],
            OrderStatus.PickedUp => [Dispatch, Assign, PickUp],
            OrderStatus.Delivered => [Dispatch, Assign, PickUp, Deliver],
            OrderStatus.DeliveryFailed => [Dispatch, Assign, PickUp, Fail],
            OrderStatus.Returned => [Dispatch, Assign, PickUp, Fail, Return],
            OrderStatus.Cancelled => [Cancel],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }
}
