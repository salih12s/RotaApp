namespace Rota.Contracts.Events.Orders;

/// <summary>
/// <c>OrderStatusChanged.v1</c>: one event for every transition (ADR-011); consumers switch on
/// <see cref="ToStatus"/> and drop events whose <see cref="OrderVersion"/> they have already seen.
/// </summary>
public sealed record OrderStatusChangedV1(
    Guid OrderId,
    string MerchantId,
    string MerchantOrderRef,
    string? FromStatus,
    string ToStatus,
    Guid? CourierId,
    string? Reason,
    DateTimeOffset OccurredAt,
    int OrderVersion) : IIntegrationEvent
{
    public static string EventType => "OrderStatusChanged.v1";

    public static string RoutingKey => "orders.order-status-changed.v1";
}
