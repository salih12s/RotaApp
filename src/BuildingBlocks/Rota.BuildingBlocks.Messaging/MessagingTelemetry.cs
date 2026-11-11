using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rota.BuildingBlocks.Messaging;

/// <summary>Trace source and counters for the messaging layer; registered in the service defaults.</summary>
public static class MessagingTelemetry
{
    public const string Name = "Rota.Messaging";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter _meter = new(Name);

    public static readonly Counter<long> Published = _meter.CreateCounter<long>("rota.outbox.published", description: "Outbox rows published and confirmed");
    public static readonly Counter<long> PublishFailed = _meter.CreateCounter<long>("rota.outbox.publish_failed", description: "Outbox publish attempts that failed");
    public static readonly Counter<long> Consumed = _meter.CreateCounter<long>("rota.consumer.processed", description: "Messages handled");
    public static readonly Counter<long> Duplicates = _meter.CreateCounter<long>("rota.consumer.duplicates", description: "Redeliveries dropped by the inbox");
    public static readonly Counter<long> Retried = _meter.CreateCounter<long>("rota.consumer.retried", description: "Failed messages sent to a retry queue");
    public static readonly Counter<long> Ignored = _meter.CreateCounter<long>("rota.consumer.ignored", description: "Valid messages a handler deliberately skipped (e.g. order already cancelled)");
    public static readonly Counter<long> DeadLettered = _meter.CreateCounter<long>("rota.consumer.dead_lettered", description: "Messages parked in a DLQ");
}
