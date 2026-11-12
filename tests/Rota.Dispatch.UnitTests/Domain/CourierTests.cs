using Rota.BuildingBlocks.Primitives;
using Rota.Dispatch.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Dispatch;

namespace Rota.Dispatch.UnitTests.Domain;

public sealed class CourierTests
{
    // Copied from docs/02-domain.md §5.2, independent of CourierTransitions.
    private static readonly Dictionary<(CourierState From, CourierAction Action), CourierState> _allowed = new()
    {
        [(CourierState.Offline, CourierAction.StartShift)] = CourierState.Available,
        [(CourierState.Available, CourierAction.EndShift)] = CourierState.Offline,
        [(CourierState.Available, CourierAction.ReceiveOffer)] = CourierState.Offered,
        [(CourierState.Offered, CourierAction.AcceptOffer)] = CourierState.OnDelivery,
        [(CourierState.Offered, CourierAction.ReleaseOffer)] = CourierState.Available,
        [(CourierState.OnDelivery, CourierAction.FinishDelivery)] = CourierState.Available,
    };

    // Which catalog code a rejected pair must return (06 §5).
    private static string ExpectedRejection(CourierState from, CourierAction action) => (from, action) switch
    {
        (CourierState.Offered or CourierState.OnDelivery, CourierAction.EndShift) => "courier-busy",
        (_, CourierAction.ReceiveOffer) => "courier-not-available",
        _ => "invalid-courier-transition",
    };

    public static TheoryData<CourierState, CourierAction> AllPairs()
    {
        var data = new TheoryData<CourierState, CourierAction>();
        foreach (var state in Enum.GetValues<CourierState>())
        {
            foreach (var action in Enum.GetValues<CourierAction>())
            {
                data.Add(state, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Action_InEveryState_FollowsTransitionTable(CourierState from, CourierAction action)
    {
        // PASS criterion 2 (courier half).
        var built = new CourierBuilder().InState(from).Build();
        var courier = built.Courier;

        var result = Apply(built, action, TestClock.Start);

        if (_allowed.TryGetValue((from, action), out var to))
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            courier.State.ShouldBe(to);
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBe(ExpectedRejection(from, action));
            courier.State.ShouldBe(from);
        }
    }

    [Fact]
    public void ReceiveOffer_ThenAccept_KeepsOrderAndClearsOffer()
    {
        var courier = new CourierBuilder().InState(CourierState.Available).Build().Courier;
        var offerId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        courier.ReceiveOffer(offerId, orderId, TestClock.Start).IsSuccess.ShouldBeTrue();
        courier.CurrentOfferId.ShouldBe(offerId);

        courier.AcceptOffer(offerId, TestClock.Start).IsSuccess.ShouldBeTrue();
        courier.CurrentOfferId.ShouldBeNull();
        courier.CurrentOrderId.ShouldBe(orderId);
    }

    [Fact]
    public void ReleaseOffer_ClearsOfferAndOrder()
    {
        var built = new CourierBuilder().InState(CourierState.Offered).Build();

        built.Courier.ReleaseOffer(built.OfferId, TestClock.Start).IsSuccess.ShouldBeTrue();

        built.Courier.CurrentOfferId.ShouldBeNull();
        built.Courier.CurrentOrderId.ShouldBeNull();
    }

    [Fact]
    public void AcceptOffer_WithAnotherOffer_IsRejectedAndStateUnchanged()
    {
        // A late answer to an old offer must not attach the courier to someone else's work.
        var built = new CourierBuilder().InState(CourierState.Offered).Build();

        var result = built.Courier.AcceptOffer(Guid.CreateVersion7(), TestClock.Start);

        result.Error.ShouldNotBeNull().Code.ShouldBe("invalid-courier-transition");
        built.Courier.State.ShouldBe(CourierState.Offered);
        built.Courier.CurrentOfferId.ShouldBe(built.OfferId);
    }

    [Fact]
    public void ReleaseOffer_WithAnotherOffer_IsRejected()
    {
        var built = new CourierBuilder().InState(CourierState.Offered).Build();

        built.Courier.ReleaseOffer(Guid.CreateVersion7(), TestClock.Start)
            .Error.ShouldNotBeNull().Code.ShouldBe("invalid-courier-transition");
        built.Courier.State.ShouldBe(CourierState.Offered);
    }

    [Fact]
    public void FinishDelivery_ForAnotherOrder_IsRejected()
    {
        var built = new CourierBuilder().InState(CourierState.OnDelivery).Build();

        built.Courier.FinishDelivery(Guid.CreateVersion7(), TestClock.Start)
            .Error.ShouldNotBeNull().Code.ShouldBe("invalid-courier-transition");
        built.Courier.State.ShouldBe(CourierState.OnDelivery);
    }

    [Fact]
    public void StartAndEndShift_SetAndClearShiftStart()
    {
        var time = TestClock.Create();
        var courier = new CourierBuilder().WithClock(time).Build().Courier;

        courier.StartShift(time.GetUtcNow()).IsSuccess.ShouldBeTrue();
        courier.ShiftStartedAt.ShouldBe(time.GetUtcNow());

        courier.EndShift(time.GetUtcNow()).IsSuccess.ShouldBeTrue();
        courier.ShiftStartedAt.ShouldBeNull();
    }

    private static Result Apply(BuiltCourier built, CourierAction action, DateTimeOffset now)
    {
        var courier = built.Courier;
        return action switch
        {
            CourierAction.StartShift => courier.StartShift(now),
            CourierAction.EndShift => courier.EndShift(now),
            CourierAction.ReceiveOffer => courier.ReceiveOffer(Guid.CreateVersion7(), Guid.CreateVersion7(), now),
            CourierAction.AcceptOffer => courier.AcceptOffer(built.OfferId, now),
            CourierAction.ReleaseOffer => courier.ReleaseOffer(built.OfferId, now),
            CourierAction.FinishDelivery => courier.FinishDelivery(built.OrderId, now),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    [Fact]
    public void Version_CountsOnlySuccessfulTransitions()
    {
        var built = new CourierBuilder().InState(CourierState.Available).Build();
        var courier = built.Courier;
        var versionBefore = courier.Version;

        courier.AcceptOffer(Guid.CreateVersion7(), TestClock.Start).IsFailure.ShouldBeTrue();
        courier.Version.ShouldBe(versionBefore, "a rejected action changes nothing");

        courier.EndShift(TestClock.Start).IsSuccess.ShouldBeTrue();
        courier.Version.ShouldBe(versionBefore + 1);
    }
}
