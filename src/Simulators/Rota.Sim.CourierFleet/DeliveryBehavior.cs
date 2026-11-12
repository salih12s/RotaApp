namespace Rota.Sim.CourierFleet;

public enum DeliveryPlan
{
    /// <summary>Picks up, enters the customer's code correctly, done.</summary>
    Deliver,

    /// <summary>Mistypes the code once (the server must answer 400 and count it), then enters the right one.</summary>
    MistypeThenDeliver,

    /// <summary>Nobody opens the door: reports "delivery failed", then brings the parcel back ("returned").</summary>
    FailAndReturn,
}

/// <summary>
/// What a pretend courier does with an order it accepted (simulator v3), as plain arithmetic so it can be unit-tested exactly:
/// a dice roll in [0, 1) picks the plan, and a wrong code is always a well-formed 4-digit code that is not the right one.
/// </summary>
/// <param name="MistypeRate">Chance to mistype the code once before getting it right, 0..1.</param>
/// <param name="FailRate">Chance the customer is not there (delivery fails and the parcel returns), 0..1.</param>
public sealed record DeliveryBehavior(double MistypeRate, double FailRate)
{
    public DeliveryPlan Decide(double roll)
    {
        if (roll < FailRate)
        {
            return DeliveryPlan.FailAndReturn;
        }

        return roll < FailRate + MistypeRate ? DeliveryPlan.MistypeThenDeliver : DeliveryPlan.Deliver;
    }

    /// <summary>A code that is well-formed (4 digits) but differs from <paramref name="correct"/>: one digit is changed.</summary>
    public static string Mistype(string correct)
    {
        ArgumentException.ThrowIfNullOrEmpty(correct);

        var digits = correct.ToCharArray();
        digits[0] = digits[0] == '9' ? '0' : (char)(digits[0] + 1);
        return new string(digits);
    }
}
