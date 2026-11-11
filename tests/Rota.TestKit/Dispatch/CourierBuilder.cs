using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Primitives;
using Rota.Dispatch.Api.Domain;

namespace Rota.TestKit.Dispatch;

/// <summary>A courier built for a test; OfferId/OrderId are set when the state has them.</summary>
public sealed record BuiltCourier(Courier Courier, Guid OfferId, Guid OrderId);

/// <summary>
/// Builds a <see cref="Courier"/> in any state by walking the real transitions, like OrderBuilder.
/// Each step advances the clock by 1 second.
/// </summary>
public sealed class CourierBuilder
{
    private static readonly TimeSpan _step = TimeSpan.FromSeconds(1);

    private CourierState _state = CourierState.Offline;
    private Guid _courierId = Guid.CreateVersion7();
    private FakeTimeProvider _time = TestClock.Create();

    public CourierBuilder InState(CourierState state)
    {
        _state = state;
        return this;
    }

    public CourierBuilder WithId(Guid courierId)
    {
        _courierId = courierId;
        return this;
    }

    public CourierBuilder WithClock(FakeTimeProvider time)
    {
        _time = time;
        return this;
    }

    public BuiltCourier Build()
    {
        var courier = Courier.Register(_courierId, "Test Courier", _time.GetUtcNow());
        var offerId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        Result Start() => courier.StartShift(_time.GetUtcNow());
        Result Offer() => courier.ReceiveOffer(offerId, orderId, _time.GetUtcNow());
        Result Accept() => courier.AcceptOffer(offerId, _time.GetUtcNow());

        List<Func<Result>> path = _state switch
        {
            CourierState.Offline => [],
            CourierState.Available => [Start],
            CourierState.Offered => [Start, Offer],
            CourierState.OnDelivery => [Start, Offer, Accept],
            _ => throw new ArgumentOutOfRangeException(nameof(_state), _state, null),
        };

        foreach (var step in path)
        {
            _time.Advance(_step);
            var result = step();
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"CourierBuilder could not reach {_state}: {result.Error.Code} ({result.Error.Message})");
            }
        }

        return new BuiltCourier(courier, offerId, orderId);
    }
}
