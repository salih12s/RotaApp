using System.Text.RegularExpressions;
using Rota.BuildingBlocks.Messaging;
using Rota.Contracts.Events;
using Rota.Contracts.Events.Dispatch;
using Rota.Contracts.Events.Orders;

namespace Rota.Contracts.Tests;

public sealed partial class EventContractTests
{
    private static readonly Guid _orderId = Guid.Parse("01933c5e-0000-7000-8000-000000000001");
    private static readonly Guid _courierId = Guid.Parse("01933c5e-0000-7000-8000-000000000002");
    private static readonly Guid _offerId = Guid.Parse("01933c5e-0000-7000-8000-000000000003");
    private static readonly Guid _eventId = Guid.Parse("01933c5e-0000-7000-8000-0000000000ff");
    private static readonly DateTimeOffset _at = new(2026, 11, 11, 10, 31, 5, TimeSpan.Zero);

    private static readonly LocationV1 _pickup = new(40.9903, 29.029, "Kadıköy Şube, Moda Cd. 10");
    private static readonly LocationV1 _dropoff = new(40.985, 29.04, "Caferağa Mh. Moda");

    /// <summary>One fully populated sample per event. Add the sample here when adding an event.</summary>
    public static TheoryData<object> Samples() =>
    [
        new OrderCreatedV1(_orderId, "merchant-1", "PZ-2026-000123", _pickup, _dropoff, 1120, _at, 1),
        new OrderStatusChangedV1(_orderId, "merchant-1", "PZ-2026-000123", "Created", "Dispatching", null, null, _at, 2),
        new DispatchStartedV1(_orderId, _at),
        new CourierAssignedV1(_orderId, _courierId, _offerId, "Auto", 420, _at),
        new DispatchFailedV1(_orderId, "NoCandidates", _at),
        new CourierStateChangedV1(_courierId, "Ali K.", "Available", null, _at, 3),
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Envelope_OfEachEvent_MatchesItsSnapshot(object data)
    {
        var type = data.GetType();
        var eventType = (string)type.GetProperty(nameof(IIntegrationEvent.EventType))!.GetValue(null)!;
        var envelope = new EventEnvelope<object>(_eventId, eventType, _at, _orderId, 1, data);

        SnapshotVerifier.Verify(type.Name, envelope);
    }

    [Fact]
    public void EveryEventType_HasASample_SoNoContractIsLeftUnpinned()
    {
        var all = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IIntegrationEvent)))
            .ToHashSet();
        var sampled = Samples().Select(s => s.Data.GetType()).ToHashSet();

        all.Except(sampled).ShouldBeEmpty("These events have no snapshot sample");
    }

    [Fact]
    public void EveryEvent_FollowsNamingConvention_AndSnapshotFilesAreNotOrphans()
    {
        var types = Samples().Select(s => s.Data.GetType()).ToList();
        foreach (var type in types)
        {
            var eventType = (string)type.GetProperty(nameof(IIntegrationEvent.EventType))!.GetValue(null)!;
            var routingKey = (string)type.GetProperty(nameof(IIntegrationEvent.RoutingKey))!.GetValue(null)!;

            // Type "OrderCreated.v1" ↔ class OrderCreatedV1 ↔ key "orders.order-created.v1".
            eventType.ShouldMatch(@"^[A-Z][A-Za-z]+\.v[1-9][0-9]*$");
            type.Name.ShouldBe(eventType.Replace(".v", "V", StringComparison.Ordinal));
            RoutingKeyPattern().IsMatch(routingKey).ShouldBeTrue($"{routingKey} must look like <service>.<event-name>.v<n>");
            routingKey.ShouldEndWith("." + eventType[(eventType.IndexOf('.', StringComparison.Ordinal) + 1)..]);
        }

        var orphans = Directory.EnumerateFiles(SnapshotVerifier.SnapshotDirectory(), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !n!.EndsWith(".received", StringComparison.Ordinal))
            .Except(types.Select(t => t.Name));
        orphans.ShouldBeEmpty("Snapshot files whose event no longer exists");
    }

    [GeneratedRegex(@"^[a-z]+\.[a-z]+(-[a-z]+)*\.v[1-9][0-9]*$")]
    private static partial Regex RoutingKeyPattern();
}
