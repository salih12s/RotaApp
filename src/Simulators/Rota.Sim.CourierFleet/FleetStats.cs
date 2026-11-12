namespace Rota.Sim.CourierFleet;

/// <summary>Counters shared by every simulated courier. Thread-safe.</summary>
public sealed class FleetStats
{
    private readonly object _probeLock = new();
    private readonly List<double> _probeMilliseconds = [];

    private long _sent;
    private long _accepted;
    private long _outOfOrder;
    private long _streamErrors;
    private long _disconnects;
    private long _probeErrors;
    private long _offersSeen;
    private long _offersAccepted;
    private long _offersRejected;
    private long _offersIgnored;
    private long _offersWithdrawn;
    private long _answersRefused;
    private long _answersFailed;
    private long _offerStreamErrors;
    private long _pickedUp;
    private long _delivered;
    private long _wrongCodesRejected;
    private long _deliveryFailed;
    private long _returned;
    private long _codeMissing;
    private long _deliveryCallsFailed;
    private long _pickupRetries;

    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>What the server reported when each stream ended: points that became the current position.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>What the server reported when each stream ended: points that were not newer than the current one.</summary>
    public long OutOfOrder => Interlocked.Read(ref _outOfOrder);

    public long StreamErrors => Interlocked.Read(ref _streamErrors);

    public long Disconnects => Interlocked.Read(ref _disconnects);

    public long ProbeErrors => Interlocked.Read(ref _probeErrors);

    public long OffersSeen => Interlocked.Read(ref _offersSeen);

    public long OffersAccepted => Interlocked.Read(ref _offersAccepted);

    public long OffersRejected => Interlocked.Read(ref _offersRejected);

    /// <summary>Offers the courier decided not to answer at all.</summary>
    public long OffersIgnored => Interlocked.Read(ref _offersIgnored);

    public long OffersWithdrawn => Interlocked.Read(ref _offersWithdrawn);

    /// <summary>Answers Dispatch refused with 409: the offer had already expired or the order was cancelled.</summary>
    public long AnswersRefused => Interlocked.Read(ref _answersRefused);

    public long AnswersFailed => Interlocked.Read(ref _answersFailed);

    public long OfferStreamErrors => Interlocked.Read(ref _offerStreamErrors);

    public long ParcelsPickedUp => Interlocked.Read(ref _pickedUp);

    public long ParcelsDelivered => Interlocked.Read(ref _delivered);

    /// <summary>Deliberately mistyped codes the server answered with 400 (the lock counter doing its job).</summary>
    public long WrongCodesRejected => Interlocked.Read(ref _wrongCodesRejected);

    public long DeliveriesFailed => Interlocked.Read(ref _deliveryFailed);

    public long ParcelsReturned => Interlocked.Read(ref _returned);

    /// <summary>Orders whose code never reached the courier (the parcel stays out).</summary>
    public long CodesMissing => Interlocked.Read(ref _codeMissing);

    public long DeliveryCallsFailed => Interlocked.Read(ref _deliveryCallsFailed);

    /// <summary>Times a pickup was asked again because Orders had not yet recorded the assignment (eventual consistency).</summary>
    public long PickupRetries => Interlocked.Read(ref _pickupRetries);

    public void PickupRetried() => Interlocked.Increment(ref _pickupRetries);

    public void ParcelPickedUp() => Interlocked.Increment(ref _pickedUp);

    public void ParcelDelivered() => Interlocked.Increment(ref _delivered);

    public void WrongCodeRejected() => Interlocked.Increment(ref _wrongCodesRejected);

    public void DeliveryFailed() => Interlocked.Increment(ref _deliveryFailed);

    public void ParcelReturned() => Interlocked.Increment(ref _returned);

    public void CodeMissing() => Interlocked.Increment(ref _codeMissing);

    public void DeliveryCallFailed() => Interlocked.Increment(ref _deliveryCallsFailed);

    public void OfferSeen() => Interlocked.Increment(ref _offersSeen);

    public void OfferIgnored() => Interlocked.Increment(ref _offersIgnored);

    public void OfferWithdrawn() => Interlocked.Increment(ref _offersWithdrawn);

    public void OfferAnswerRefused() => Interlocked.Increment(ref _answersRefused);

    public void OfferAnswerFailed() => Interlocked.Increment(ref _answersFailed);

    public void OfferStreamFailed() => Interlocked.Increment(ref _offerStreamErrors);

    public void OfferAnswered(OfferDecision decision)
    {
        if (decision == OfferDecision.Accept)
        {
            Interlocked.Increment(ref _offersAccepted);
        }
        else
        {
            Interlocked.Increment(ref _offersRejected);
        }
    }

    public void PointSent() => Interlocked.Increment(ref _sent);

    public void StreamEnded(long accepted, long outOfOrder)
    {
        Interlocked.Add(ref _accepted, accepted);
        Interlocked.Add(ref _outOfOrder, outOfOrder);
    }

    public void StreamFailed() => Interlocked.Increment(ref _streamErrors);

    public void WentOffline() => Interlocked.Increment(ref _disconnects);

    public void ProbeFailed() => Interlocked.Increment(ref _probeErrors);

    public void ProbeAnswered(double milliseconds)
    {
        lock (_probeLock)
        {
            _probeMilliseconds.Add(milliseconds);
        }
    }

    /// <summary>Percentiles (p in 0..100) of the probe latencies so far; <c>null</c> when there are none.</summary>
    public (int Count, double P50, double P95, double P99, double Max)? ProbeSummary()
    {
        double[] sorted;
        lock (_probeLock)
        {
            sorted = [.. _probeMilliseconds.Order()];
        }

        return sorted.Length == 0 ? null : (sorted.Length, Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an already sorted array.</summary>
    public static double Percentile(double[] sorted, double percent)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        var rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
