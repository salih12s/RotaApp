using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Consumers;

/// <summary>
/// <c>OrderCreated.v1</c> → a new dispatch job in <c>Searching</c> plus <c>DispatchStarted.v1</c> in the outbox.
/// Runs inside the consumer's transaction: job, event and inbox row commit together or not at all.
/// </summary>
public sealed class OrderCreatedHandler(DispatchDbContext db, TimeProvider time) : IMessageHandler<OrderCreatedV1>
{
    public Task HandleAsync(EventEnvelope<OrderCreatedV1> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var order = message.Data;
        var now = time.GetUtcNow();

        db.DispatchJobs.Add(DispatchJob.Start(order.OrderId, order.MerchantId, order.Pickup.Lat, order.Pickup.Lon, now));
        db.AddToOutbox(new DispatchStartedV1(order.OrderId, now), order.OrderId, aggregateVersion: 1, now);

        return Task.CompletedTask;
    }
}
