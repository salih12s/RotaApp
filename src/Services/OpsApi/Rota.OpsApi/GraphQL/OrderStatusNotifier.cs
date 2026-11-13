using HotChocolate.Subscriptions;
using Rota.Contracts.Events.Orders;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

/// <summary>What <c>orderStatusChanged</c> sends the panel (docs/05 §5).</summary>
public sealed record OrderStatusChangedEvent(
    Guid OrderId,
    string MerchantId,
    string MerchantOrderRef,
    OrderStatus? FromStatus,
    OrderStatus ToStatus,
    Guid? CourierId,
    DateTimeOffset OccurredAt,
    int OrderVersion)
{
    public static OrderStatusChangedEvent From(OrderStatusChangedV1 change) => new(
        change.OrderId,
        change.MerchantId,
        change.MerchantOrderRef,
        Enum.TryParse<OrderStatus>(change.FromStatus, out var from) ? from : null,
        Enum.Parse<OrderStatus>(change.ToStatus),
        change.CourierId,
        change.OccurredAt,
        change.OrderVersion);
}

/// <summary>
/// Tells every Ops API instance's subscribers that an order moved (docs/05 §5). RabbitMQ gives the event to <b>one</b> instance; that one sends it through
/// Redis, and each instance (it included) passes it to its own WebSocket clients. Best effort: the read model is already correct, so if Redis is down
/// or slow the live panel misses one update (and refreshes on its next query) but the message is not failed or retried because of it.
/// </summary>
public sealed partial class OrderStatusNotifier(ITopicEventSender sender, ILogger<OrderStatusNotifier> logger)
{
    public const string Topic = "order-status-changed";

    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(1);

    public async Task PublishAsync(OrderStatusChangedV1 change, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(Topic, OrderStatusChangedEvent.From(change), cancellationToken).AsTask().WaitAsync(_patience, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotNotified(logger, change.OrderId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live subscribers were not told about order {OrderId}; the read model is up to date")]
    private static partial void LogNotNotified(ILogger logger, Guid orderId, Exception exception);
}
