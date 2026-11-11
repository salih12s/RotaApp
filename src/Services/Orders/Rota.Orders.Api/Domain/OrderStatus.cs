using System.Collections.Frozen;

namespace Rota.Orders.Api.Domain;

public enum OrderStatus
{
    Created,
    Dispatching,
    Unassigned,
    Assigned,
    PickedUp,
    Delivered,
    DeliveryFailed,
    Returned,
    Cancelled,
}

public enum OrderAction
{
    MarkDispatching,
    Assign,
    MarkUnassigned,
    Cancel,
    PickUp,
    Deliver,
    FailDelivery,
    MarkReturned,
}

/// <summary>
/// The single source of truth for order transitions (docs/02-domain.md §5.1, invariant I-4).
/// Any (status, action) pair missing here is an invalid transition.
/// </summary>
public static class OrderTransitions
{
    private static readonly FrozenDictionary<(OrderStatus From, OrderAction Action), OrderStatus> _table =
        new Dictionary<(OrderStatus, OrderAction), OrderStatus>
        {
            [(OrderStatus.Created, OrderAction.MarkDispatching)] = OrderStatus.Dispatching,
            [(OrderStatus.Unassigned, OrderAction.MarkDispatching)] = OrderStatus.Dispatching,

            [(OrderStatus.Dispatching, OrderAction.Assign)] = OrderStatus.Assigned,
            [(OrderStatus.Unassigned, OrderAction.Assign)] = OrderStatus.Assigned,

            [(OrderStatus.Dispatching, OrderAction.MarkUnassigned)] = OrderStatus.Unassigned,

            [(OrderStatus.Created, OrderAction.Cancel)] = OrderStatus.Cancelled,
            [(OrderStatus.Dispatching, OrderAction.Cancel)] = OrderStatus.Cancelled,
            [(OrderStatus.Unassigned, OrderAction.Cancel)] = OrderStatus.Cancelled,
            [(OrderStatus.Assigned, OrderAction.Cancel)] = OrderStatus.Cancelled,

            [(OrderStatus.Assigned, OrderAction.PickUp)] = OrderStatus.PickedUp,
            [(OrderStatus.PickedUp, OrderAction.Deliver)] = OrderStatus.Delivered,
            [(OrderStatus.PickedUp, OrderAction.FailDelivery)] = OrderStatus.DeliveryFailed,
            [(OrderStatus.DeliveryFailed, OrderAction.MarkReturned)] = OrderStatus.Returned,
        }.ToFrozenDictionary();

    public static bool TryGetTarget(OrderStatus from, OrderAction action, out OrderStatus to) =>
        _table.TryGetValue((from, action), out to);
}
