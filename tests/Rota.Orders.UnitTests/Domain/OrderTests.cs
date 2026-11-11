using Rota.Orders.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.UnitTests.Domain;

public sealed class OrderTests
{
    [Fact]
    public void Create_WithinServiceArea_StartsCreatedWithVersion1AndHistory()
    {
        var now = TestClock.Start;

        var result = Order.Create(Guid.CreateVersion7(), "merchant-a", OrderTestData.Kadikoy, OrderTestData.Moda,
            ServiceAreaPolicy.Default, OrderTestData.Hasher, now);

        var order = result.Value.Order;
        order.Status.ShouldBe(OrderStatus.Created);
        order.Version.ShouldBe(1);
        order.CreatedAt.ShouldBe(now);
        order.DistanceMeters.ShouldBeInRange(950, 1000);
        order.History.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            h => h.From.ShouldBeNull(),
            h => h.To.ShouldBe(OrderStatus.Created),
            h => h.Actor.ShouldBe(OrderActor.Merchant("merchant-a")));
    }

    [Fact]
    public void Create_BeyondServiceArea_ReturnsOutOfServiceArea()
    {
        var farAway = GeoPoint.Create(39.9208, 32.8541).Value; // Ankara

        var result = Order.Create(Guid.CreateVersion7(), "merchant-a", OrderTestData.Kadikoy, farAway,
            ServiceAreaPolicy.Default, OrderTestData.Hasher, TestClock.Start);

        result.Error.ShouldNotBeNull().Code.ShouldBe("out-of-service-area");
    }

    [Fact]
    public void Assign_FromDispatching_RecordsCourierActorAndTime()
    {
        var time = TestClock.Create();
        var order = new OrderBuilder().InStatus(OrderStatus.Dispatching).WithClock(time).Build().Order;
        var courierId = Guid.CreateVersion7();
        time.Advance(TimeSpan.FromSeconds(42));

        order.Assign(courierId, OrderActor.System, time.GetUtcNow()).IsSuccess.ShouldBeTrue();

        order.AssignedCourierId.ShouldBe(courierId);
        order.UpdatedAt.ShouldBe(time.GetUtcNow());
        order.History[^1].OccurredAt.ShouldBe(time.GetUtcNow());
    }

    [Fact]
    public void Assign_WhenCancelled_KeepsCourierEmpty()
    {
        // The CourierAssigned consumer (F03/F05) turns this error into "ignore + log"; the domain stays strict.
        var order = new OrderBuilder().InStatus(OrderStatus.Cancelled).Build().Order;

        var result = order.Assign(Guid.CreateVersion7(), OrderActor.System, TestClock.Start);

        result.Error.ShouldNotBeNull().Code.ShouldBe("invalid-order-transition");
        order.AssignedCourierId.ShouldBeNull();
    }

    [Fact]
    public void Cancel_WithReason_StoresReasonAndActorInHistory()
    {
        var order = new OrderBuilder().InStatus(OrderStatus.Assigned).ForMerchant("merchant-b").Build().Order;

        order.Cancel(OrderActor.Merchant("merchant-b"), "customer changed mind", TestClock.Start).IsSuccess.ShouldBeTrue();

        order.History[^1].ShouldSatisfyAllConditions(
            h => h.Reason.ShouldBe("customer changed mind"),
            h => h.Actor.Type.ShouldBe(OrderActorType.Merchant));
    }

    [Fact]
    public void InvalidTransition_ErrorMessage_NamesCurrentStatus()
    {
        var order = new OrderBuilder().InStatus(OrderStatus.PickedUp).Build().Order;

        var result = order.Cancel(OrderActor.Merchant(order.MerchantId), null, TestClock.Start);

        // ProblemDetails needs currentStatus (06 §5); the endpoint reads it from the order, the message names it too.
        result.Error.ShouldNotBeNull().Message.ShouldContain("PickedUp");
    }
}
