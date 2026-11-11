using System.Text;
using Rota.BuildingBlocks.Primitives;
using Rota.Orders.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.UnitTests.Domain;

public sealed class DeliveryCodeTests
{
    private static BuiltOrder PickedUpOrder() => new OrderBuilder().InStatus(OrderStatus.PickedUp).Build();

    private static string WrongCodeFor(string code) =>
        code == "0000" ? "1111" : "0000";

    private static Result Deliver(BuiltOrder built, string code) =>
        built.Order.Deliver(code, OrderTestData.Hasher, OrderActor.Courier(built.CourierId), TestClock.Start);

    [Fact]
    public void Generate_ReturnsFourDigitsAndStoresOnlyHmac()
    {
        var (code, plainText) = DeliveryCode.Generate(OrderTestData.Hasher);

        DeliveryCode.IsWellFormed(plainText).ShouldBeTrue();
        code.Hash.ShouldNotContain(plainText);
        code.Hash.Length.ShouldBe(64); // SHA-256 → 32 bytes → 64 hex chars
        code.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public void Hash_WithDifferentKey_IsDifferent()
    {
        // The whole point of HMAC: without the server key the hash cannot be recomputed.
        var otherKey = new DeliveryCodeHasher(Encoding.ASCII.GetBytes("another-test-key-of-32-bytes-len"));

        otherKey.Hash("4821").ShouldNotBe(OrderTestData.Hasher.Hash("4821"));
    }

    [Fact]
    public void Hasher_WithShortKey_Throws() =>
        Should.Throw<ArgumentException>(() => new DeliveryCodeHasher(new byte[16]));

    [Theory]
    [InlineData("4821", true)]
    [InlineData("0000", true)]
    [InlineData("482", false)]
    [InlineData("48210", false)]
    [InlineData("48a1", false)]
    [InlineData("٤٨٢١", false)] // Arabic-Indic digits: char.IsDigit says yes, we accept ASCII only
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormed_AcceptsExactlyFourAsciiDigits(string? candidate, bool expected) =>
        DeliveryCode.IsWellFormed(candidate).ShouldBe(expected);

    [Fact]
    public void Deliver_WithWrongCode_ReturnsInvalidAndCountsAttempt()
    {
        var built = PickedUpOrder();

        var result = Deliver(built, WrongCodeFor(built.DeliveryCode));

        result.Error.ShouldNotBeNull().Code.ShouldBe("invalid-delivery-code");
        built.Order.DeliveryCode.RemainingAttempts.ShouldBe(4);
        built.Order.Status.ShouldBe(OrderStatus.PickedUp);
    }

    [Fact]
    public void Deliver_AfterFiveWrongCodes_RejectsEvenTheCorrectCode()
    {
        // PASS criterion 4 (first half).
        var built = PickedUpOrder();
        var wrong = WrongCodeFor(built.DeliveryCode);
        for (var i = 1; i <= 4; i++)
        {
            Deliver(built, wrong).Error.ShouldNotBeNull().Code.ShouldBe("invalid-delivery-code");
            built.Order.DeliveryCode.IsLocked.ShouldBeFalse($"locked too early, after attempt {i}");
        }

        // The 5th wrong code is still "wrong" (400) and is the one that locks.
        Deliver(built, wrong).Error.ShouldNotBeNull().Code.ShouldBe("invalid-delivery-code");
        built.Order.DeliveryCode.IsLocked.ShouldBeTrue();

        Deliver(built, built.DeliveryCode).Error.ShouldNotBeNull().Code.ShouldBe("delivery-code-locked");
        built.Order.Status.ShouldBe(OrderStatus.PickedUp);
    }

    [Fact]
    public void Deliver_AfterUnlock_AcceptsTheCorrectCode()
    {
        // PASS criterion 4 (second half).
        var built = PickedUpOrder();
        for (var i = 0; i < DeliveryCode.MaxFailedAttempts; i++)
        {
            Deliver(built, WrongCodeFor(built.DeliveryCode));
        }

        built.Order.UnlockDeliveryCode(TestClock.Start).IsSuccess.ShouldBeTrue();

        built.Order.DeliveryCode.RemainingAttempts.ShouldBe(DeliveryCode.MaxFailedAttempts);
        Deliver(built, built.DeliveryCode).IsSuccess.ShouldBeTrue();
        built.Order.Status.ShouldBe(OrderStatus.Delivered);
    }

    [Fact]
    public void Deliver_WithMalformedCode_DoesNotConsumeAttempt()
    {
        var built = PickedUpOrder();

        Deliver(built, "48a1").Error.ShouldNotBeNull().Code.ShouldBe("validation-failed");

        built.Order.DeliveryCode.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public void Deliver_BeforePickup_DoesNotConsumeAttempt()
    {
        var built = new OrderBuilder().InStatus(OrderStatus.Assigned).Build();

        Deliver(built, WrongCodeFor(built.DeliveryCode)).Error.ShouldNotBeNull().Code.ShouldBe("invalid-order-transition");

        built.Order.DeliveryCode.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public void UnlockDeliveryCode_WhenNotLocked_IsIdempotentSuccess()
    {
        var built = PickedUpOrder();

        built.Order.UnlockDeliveryCode(TestClock.Start).IsSuccess.ShouldBeTrue();
        built.Order.UnlockDeliveryCode(TestClock.Start).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(OrderStatus.Assigned)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void UnlockDeliveryCode_OutsidePickedUp_ReturnsInvalidTransition(OrderStatus status)
    {
        var order = new OrderBuilder().InStatus(status).Build().Order;

        order.UnlockDeliveryCode(TestClock.Start).Error.ShouldNotBeNull().Code.ShouldBe("invalid-order-transition");
    }
}
