namespace Rota.Contracts.Events.Dispatch;

/// <summary><c>DispatchStarted.v1</c>: Dispatch picked up an order and began searching. Orders moves it to Dispatching.</summary>
public sealed record DispatchStartedV1(Guid OrderId, DateTimeOffset StartedAt) : IIntegrationEvent
{
    public static string EventType => "DispatchStarted.v1";

    public static string RoutingKey => "dispatch.dispatch-started.v1";
}

/// <summary><c>CourierAssigned.v1</c>: a courier accepted (or a dispatcher assigned) the order.</summary>
public sealed record CourierAssignedV1(
    Guid OrderId,
    Guid CourierId,
    Guid? OfferId,
    string AssignmentType,
    int EtaSeconds,
    DateTimeOffset AssignedAt) : IIntegrationEvent
{
    public static string EventType => "CourierAssigned.v1";

    public static string RoutingKey => "dispatch.courier-assigned.v1";
}

/// <summary><c>DispatchFailed.v1</c>: no courier could be found (<c>NoCandidates</c> / <c>MaxAttemptsReached</c>).</summary>
public sealed record DispatchFailedV1(Guid OrderId, string Reason, DateTimeOffset FailedAt) : IIntegrationEvent
{
    public static string EventType => "DispatchFailed.v1";

    public static string RoutingKey => "dispatch.dispatch-failed.v1";
}

/// <summary><c>CourierStateChanged.v1</c>: read-model feed for Ops API.</summary>
public sealed record CourierStateChangedV1(
    Guid CourierId,
    string DisplayName,
    string State,
    Guid? CurrentOrderId,
    DateTimeOffset OccurredAt,
    int CourierVersion) : IIntegrationEvent
{
    public static string EventType => "CourierStateChanged.v1";

    public static string RoutingKey => "dispatch.courier-state-changed.v1";
}
