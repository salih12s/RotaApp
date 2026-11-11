using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Primitives;
using Rota.Dispatch.Api.Domain;
using Rota.TestKit;

namespace Rota.Dispatch.UnitTests.Domain;

public sealed class OfferTests
{
    private static Offer NewOffer(FakeTimeProvider time) =>
        Offer.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            distanceMeters: 800, etaSeconds: 144, DispatchRules.Default, time.GetUtcNow());

    /// <summary>Brings a fresh offer into <paramref name="status"/> through real transitions.</summary>
    private static Offer OfferIn(OfferStatus status, FakeTimeProvider time)
    {
        var offer = NewOffer(time);
        var result = status switch
        {
            OfferStatus.Pending => Result.Success(),
            OfferStatus.Accepted => offer.Accept(time.GetUtcNow()),
            OfferStatus.Rejected => offer.Reject(time.GetUtcNow()),
            OfferStatus.Cancelled => offer.Cancel(),
            OfferStatus.Expired => offer.Expire(offer.ExpiresAt),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
        result.IsSuccess.ShouldBeTrue($"could not reach {status}");
        return offer;
    }

    public static TheoryData<OfferStatus, OfferAction> AllPairs()
    {
        var data = new TheoryData<OfferStatus, OfferAction>();
        foreach (var status in Enum.GetValues<OfferStatus>())
        {
            foreach (var action in Enum.GetValues<OfferAction>())
            {
                data.Add(status, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Action_InEveryStatus_OnlyPendingMoves(OfferStatus from, OfferAction action)
    {
        // PASS criterion 2 (offer half). Each action runs at a time where its clock condition holds,
        // so this test isolates the status rule; the clock rule is tested below.
        var time = TestClock.Create();
        var offer = OfferIn(from, time);
        var beforeExpiry = offer.CreatedAt + TimeSpan.FromSeconds(10);

        var result = action switch
        {
            OfferAction.Accept => offer.Accept(beforeExpiry),
            OfferAction.Reject => offer.Reject(beforeExpiry),
            OfferAction.Expire => offer.Expire(offer.ExpiresAt),
            OfferAction.Cancel => offer.Cancel(),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        if (from == OfferStatus.Pending)
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            offer.Status.ShouldBe(action switch
            {
                OfferAction.Accept => OfferStatus.Accepted,
                OfferAction.Reject => OfferStatus.Rejected,
                OfferAction.Expire => OfferStatus.Expired,
                _ => OfferStatus.Cancelled,
            });
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBe("offer-not-pending");
            offer.Status.ShouldBe(from);
        }
    }

    [Fact]
    public void Create_ExpiresAfterThirtySeconds()
    {
        var time = TestClock.Create();

        var offer = NewOffer(time);

        offer.ExpiresAt.ShouldBe(TestClock.Start + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Accept_OneTickBeforeExpiry_Succeeds()
    {
        var offer = NewOffer(TestClock.Create());

        offer.Accept(offer.ExpiresAt - TimeSpan.FromTicks(1)).IsSuccess.ShouldBeTrue();
        offer.RespondedAt.ShouldBe(offer.ExpiresAt - TimeSpan.FromTicks(1));
    }

    [Fact]
    public void Accept_ExactlyAtExpiry_ReturnsOfferNotPending()
    {
        // US-05: an expired offer → 409. At ExpiresAt the offer belongs to the timeout job.
        var time = TestClock.Create();
        var offer = NewOffer(time);
        time.Advance(DispatchRules.Default.OfferTtl);

        offer.Accept(time.GetUtcNow()).Error.ShouldNotBeNull().Code.ShouldBe("offer-not-pending");
        offer.Status.ShouldBe(OfferStatus.Pending);
    }

    [Fact]
    public void Reject_AfterExpiry_ReturnsOfferNotPending()
    {
        var offer = NewOffer(TestClock.Create());

        offer.Reject(offer.ExpiresAt).Error.ShouldNotBeNull().Code.ShouldBe("offer-not-pending");
    }

    [Fact]
    public void Expire_BeforeExpiry_ReturnsOfferNotExpired()
    {
        var offer = NewOffer(TestClock.Create());

        offer.Expire(offer.ExpiresAt - TimeSpan.FromTicks(1)).Error.ShouldNotBeNull().Code.ShouldBe("offer-not-expired");
        offer.Status.ShouldBe(OfferStatus.Pending);
    }

    [Theory]
    [InlineData(-1_000)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000)]
    public void AcceptAndExpire_AtAnyMoment_ExactlyOneCanSucceed(int offsetMs)
    {
        // The rule the F05 race relies on (02 §5.3): the two time conditions never both hold.
        var now = TestClock.Start + DispatchRules.Default.OfferTtl + TimeSpan.FromMilliseconds(offsetMs);

        var accepted = NewOffer(TestClock.Create()).Accept(now).IsSuccess;
        var expired = NewOffer(TestClock.Create()).Expire(now).IsSuccess;

        (accepted ^ expired).ShouldBeTrue($"accepted={accepted}, expired={expired}");
    }
}
