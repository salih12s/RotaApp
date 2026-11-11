namespace Rota.Contracts.Events;

/// <summary>
/// An event published between services. The name and routing key are part of the versioned
/// contract (docs/05-contracts.md §3): a published version is never changed, a new one is added.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Contract name with version, e.g. <c>OrderCreated.v1</c>.</summary>
    static abstract string EventType { get; }

    /// <summary><c>&lt;service&gt;.&lt;event-name&gt;.v&lt;version&gt;</c>, e.g. <c>orders.order-created.v1</c>.</summary>
    static abstract string RoutingKey { get; }
}
