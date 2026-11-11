using System.Globalization;

namespace Rota.Orders.Api.Domain;

public enum OrderActorType
{
    Merchant,
    Courier,
    Dispatcher,
    System,
}

/// <summary>Who caused a status change; recorded in the status history.</summary>
public readonly record struct OrderActor(OrderActorType Type, string? Id)
{
    public static OrderActor System { get; } = new(OrderActorType.System, null);

    public static OrderActor Merchant(string merchantId) => new(OrderActorType.Merchant, merchantId);

    public static OrderActor Courier(Guid courierId) =>
        new(OrderActorType.Courier, courierId.ToString("D", CultureInfo.InvariantCulture));

    public static OrderActor Dispatcher(string dispatcherId) => new(OrderActorType.Dispatcher, dispatcherId);
}

public sealed record OrderStatusChange(
    OrderStatus? From,
    OrderStatus To,
    OrderActor Actor,
    string? Reason,
    DateTimeOffset OccurredAt);
