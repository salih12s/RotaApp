namespace Rota.Sim.CourierFleet;

public enum OfferDecision
{
    Accept,
    Reject,

    /// <summary>The courier never answers (phone in a pocket, app closed): Dispatch has to notice by itself when the offer expires.</summary>
    Ignore,
}

/// <summary>
/// How a pretend courier treats an offer, as plain arithmetic: given a dice roll in [0, 1) it says accept, reject or ignore,
/// and how long the courier "thinks" first. No network here, so it can be unit-tested exactly.
/// </summary>
/// <param name="AcceptRate">Chance to accept, 0..1.</param>
/// <param name="RejectRate">Chance to reject, 0..1; what remains after accept and reject (1 - both) is "no answer".</param>
/// <param name="MinThinkTime">Shortest time before answering.</param>
/// <param name="MaxThinkTime">Longest time before answering.</param>
public sealed record OfferBehavior(double AcceptRate, double RejectRate, TimeSpan MinThinkTime, TimeSpan MaxThinkTime)
{
    public OfferDecision Decide(double roll)
    {
        if (roll < AcceptRate)
        {
            return OfferDecision.Accept;
        }

        return roll < AcceptRate + RejectRate ? OfferDecision.Reject : OfferDecision.Ignore;
    }

    /// <summary>A time between the shortest and the longest think time (the same for roll 0 and just below 1, never outside).</summary>
    public TimeSpan ThinkTime(double roll) => MinThinkTime + ((MaxThinkTime - MinThinkTime) * roll);
}
