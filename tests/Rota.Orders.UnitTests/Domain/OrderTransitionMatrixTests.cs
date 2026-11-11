using Rota.BuildingBlocks.Primitives;
using Rota.Orders.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.UnitTests.Domain;

/// <summary>
/// PASS criterion 1: every (status, action) pair. Allowed → success; anything else → invalid-order-transition.
/// The expected table is copied from docs/02-domain.md §5.1 on purpose instead of reading
/// <see cref="OrderTransitions"/>: a test that reads the code under test can never fail.
/// </summary>
public sealed class OrderTransitionMatrixTests
{
    private static readonly Dictionary<(OrderStatus From, OrderAction Action), OrderStatus> _expected = new()
    {
        [(OrderStatus.Created, OrderAction.MarkDispatching)] = OrderStatus.Dispatching,
        [(OrderStatus.Created, OrderAction.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.Dispatching, OrderAction.Assign)] = OrderStatus.Assigned,
        [(OrderStatus.Dispatching, OrderAction.MarkUnassigned)] = OrderStatus.Unassigned,
        [(OrderStatus.Dispatching, OrderAction.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.Unassigned, OrderAction.Assign)] = OrderStatus.Assigned,
        [(OrderStatus.Unassigned, OrderAction.MarkDispatching)] = OrderStatus.Dispatching,
        [(OrderStatus.Unassigned, OrderAction.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.Assigned, OrderAction.PickUp)] = OrderStatus.PickedUp,
        [(OrderStatus.Assigned, OrderAction.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.PickedUp, OrderAction.Deliver)] = OrderStatus.Delivered,
        [(OrderStatus.PickedUp, OrderAction.FailDelivery)] = OrderStatus.DeliveryFailed,
        [(OrderStatus.DeliveryFailed, OrderAction.MarkReturned)] = OrderStatus.Returned,
    };

    public static TheoryData<OrderStatus, OrderAction> AllPairs()
    {
        var data = new TheoryData<OrderStatus, OrderAction>();
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            foreach (var action in Enum.GetValues<OrderAction>())
            {
                data.Add(status, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Action_InEveryStatus_FollowsTransitionTable(OrderStatus from, OrderAction action)
    {
        var time = TestClock.Create();
        var built = new OrderBuilder().InStatus(from).WithClock(time).Build();
        var order = built.Order;
        var versionBefore = order.Version;
        var historyBefore = order.History.Count;

        var result = Apply(built, action, time.GetUtcNow());

        if (_expected.TryGetValue((from, action), out var to))
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            order.Status.ShouldBe(to);
            order.Version.ShouldBe(versionBefore + 1);
            order.History[^1].ShouldSatisfyAllConditions(
                h => h.From.ShouldBe(from),
                h => h.To.ShouldBe(to));
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBe("invalid-order-transition");
            order.Status.ShouldBe(from);
            order.Version.ShouldBe(versionBefore);
            order.History.Count.ShouldBe(historyBefore);
        }
    }

    private static Result Apply(BuiltOrder built, OrderAction action, DateTimeOffset now)
    {
        var order = built.Order;
        var courier = OrderActor.Courier(built.CourierId);

        return action switch
        {
            OrderAction.MarkDispatching => order.MarkDispatching(OrderActor.System, now),
            OrderAction.Assign => order.Assign(built.CourierId, OrderActor.System, now),
            OrderAction.MarkUnassigned => order.MarkUnassigned(OrderActor.System, now),
            OrderAction.Cancel => order.Cancel(OrderActor.Merchant(order.MerchantId), "test", now),
            OrderAction.PickUp => order.PickUp(courier, now),
            OrderAction.Deliver => order.Deliver(built.DeliveryCode, OrderTestData.Hasher, courier, now),
            OrderAction.FailDelivery => order.FailDelivery(courier, "customer absent", now),
            OrderAction.MarkReturned => order.MarkReturned(courier, now),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }
}
