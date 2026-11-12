using System.Diagnostics.Metrics;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>Dispatch metrics; the meter is registered in the service defaults.</summary>
public static class DispatchTelemetry
{
    public const string MeterName = "Rota.Dispatch";

    private static readonly Meter _meter = new(MeterName);

    /// <summary>Seconds from the dispatch job's start to the first offer reaching the database (docs/08 F05).</summary>
    public static readonly Histogram<double> TimeToFirstOffer =
        _meter.CreateHistogram<double>("rota.dispatch.time_to_first_offer", unit: "s", description: "Order accepted by Dispatch until its first offer was created");

    public static readonly Counter<long> OffersCreated = _meter.CreateCounter<long>("rota.dispatch.offers_created", description: "Offers created");

    public static readonly Counter<long> SearchPostponed = _meter.CreateCounter<long>("rota.dispatch.search_postponed", description: "Searches postponed because Tracking failed or was too slow");
}
