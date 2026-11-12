using System.Globalization;

namespace Rota.Sim.CourierFleet;

/// <summary>Command-line settings: <c>--couriers 200 --interval 3</c> and the rest below.</summary>
public sealed record FleetOptions
{
    public int Couriers { get; init; } = 200;

    /// <summary>Seconds between two positions of one courier (decimals allowed, e.g. 0.2).</summary>
    public double IntervalSeconds { get; init; } = 3;

    /// <summary>Tracking's gRPC address (plain HTTP/2 locally; see Kestrel:Endpoints:Grpc).</summary>
    public Uri Url { get; init; } = new("http://localhost:5203");

    /// <summary>Stop after this many seconds; 0 = run until Ctrl+C.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Per position, the chance that the courier loses its connection and goes offline for a few seconds.</summary>
    public double DisconnectRate { get; init; }

    /// <summary>Per position, the chance that it is delivered after the next one (out of order).</summary>
    public double ReorderRate { get; init; }

    /// <summary>Same seed, same couriers and routes: a run can be repeated.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Also ask "who is near the centre of Istanbul?" ten times a second and report how long the answers take.</summary>
    public bool ProbeNearby { get; init; }

    /// <summary>Simulator v2: the couriers also start a shift, listen for offers from Dispatch and answer them.</summary>
    public bool Offers { get; init; }

    /// <summary>Dispatch's gRPC address (offer stream); plain HTTP/2 locally, see Kestrel:Endpoints:Grpc.</summary>
    public Uri DispatchGrpcUrl { get; init; } = new("http://localhost:5202");

    /// <summary>Dispatch's REST address (shift, accept, reject).</summary>
    public Uri DispatchHttpUrl { get; init; } = new("http://localhost:5102");

    /// <summary>Keycloak realm to get tokens from (e.g. http://localhost:8180/realms/rota). Without it the dev header is sent (Development only).</summary>
    public Uri? Auth { get; init; }

    /// <summary>The courier's id: derived from seed and index, or the fixed dev-realm id when using Keycloak (the token's sub must be a user that exists).</summary>
    public Guid CourierId(int index) => Auth is null ? FleetRunner.CourierIdentity(Seed, index) : Rota.Sim.Common.SimCredentials.CourierId(index);

    /// <summary>Chance a courier accepts an offer. What is left after accept and reject is "never answers".</summary>
    public double AcceptRate { get; init; } = 0.6;

    public double RejectRate { get; init; } = 0.25;

    /// <summary>Longest time a courier takes to answer, in seconds (the shortest is half a second, or this if smaller).</summary>
    public double ResponseDelaySeconds { get; init; } = 5;

    /// <summary>Simulator v3: Orders' REST address (pickup, deliver, ...). Used only together with <see cref="CodesFile"/>.</summary>
    public Uri OrdersUrl { get; init; } = new("http://localhost:5101");

    /// <summary>JSON-lines file with the delivery codes from the merchant simulator; without it, accepted orders are not delivered (v2).</summary>
    public string? CodesFile { get; init; }

    /// <summary>Chance a courier mistypes the customer's code once before getting it right.</summary>
    public double MistypeRate { get; init; } = 0.15;

    /// <summary>Chance the customer is not there: delivery fails and the parcel is returned.</summary>
    public double FailRate { get; init; } = 0.1;

    /// <summary>Longest ride to the pickup and to the customer, in seconds (a random time up to this for each).</summary>
    public double TravelSeconds { get; init; } = 3;

    public DeliveryBehavior DeliveryBehavior => new(MistypeRate, FailRate);

    public OfferBehavior OfferBehavior => new(
        AcceptRate, RejectRate, TimeSpan.FromSeconds(Math.Min(0.5, ResponseDelaySeconds)), TimeSpan.FromSeconds(ResponseDelaySeconds));

    public static FleetOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'. Use --name value.");
            }

            var name = args[i][2..];
            // A flag without a value (--probe-nearby) means "true".
            values[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        var options = new FleetOptions();
        foreach (var (name, value) in values)
        {
            options = name switch
            {
                "couriers" => options with { Couriers = int.Parse(value, CultureInfo.InvariantCulture) },
                "interval" => options with { IntervalSeconds = double.Parse(value, CultureInfo.InvariantCulture) },
                "url" => options with { Url = new Uri(value) },
                "auth" => options with { Auth = new Uri(value) },
                "duration" => options with { DurationSeconds = int.Parse(value, CultureInfo.InvariantCulture) },
                "disconnect-rate" => options with { DisconnectRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "reorder-rate" => options with { ReorderRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "seed" => options with { Seed = int.Parse(value, CultureInfo.InvariantCulture) },
                "probe-nearby" => options with { ProbeNearby = bool.Parse(value) },
                "offers" => options with { Offers = bool.Parse(value) },
                "dispatch-grpc" => options with { DispatchGrpcUrl = new Uri(value) },
                "dispatch-http" => options with { DispatchHttpUrl = new Uri(value) },
                "accept-rate" => options with { AcceptRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "reject-rate" => options with { RejectRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "orders-url" => options with { OrdersUrl = new Uri(value) },
                "codes-file" => options with { CodesFile = value },
                "mistype-rate" => options with { MistypeRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "fail-rate" => options with { FailRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "travel" => options with { TravelSeconds = double.Parse(value, CultureInfo.InvariantCulture) },
                "response-delay" => options with { ResponseDelaySeconds = double.Parse(value, CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown option --{name}."),
            };
        }

        if (options.Couriers < 1 || options.IntervalSeconds <= 0 || options.DisconnectRate is < 0 or > 1 || options.ReorderRate is < 0 or > 1)
        {
            throw new ArgumentException("--couriers must be >= 1, --interval > 0, and the rates within [0, 1].");
        }

        if (options.AcceptRate is < 0 or > 1 || options.RejectRate is < 0 or > 1 || options.AcceptRate + options.RejectRate > 1 || options.ResponseDelaySeconds < 0)
        {
            throw new ArgumentException("--accept-rate and --reject-rate must be within [0, 1] and add up to at most 1; --response-delay must be >= 0.");
        }

        if (options.MistypeRate is < 0 or > 1 || options.FailRate is < 0 or > 1 || options.MistypeRate + options.FailRate > 1 || options.TravelSeconds < 0)
        {
            throw new ArgumentException("--mistype-rate and --fail-rate must be within [0, 1] and add up to at most 1; --travel must be >= 0.");
        }

        if (options.Auth is not null && options.Couriers > Rota.Sim.Common.SimCredentials.MaxCouriers)
        {
            throw new ArgumentException($"With --auth the dev realm has {Rota.Sim.Common.SimCredentials.MaxCouriers} simulated couriers; use --couriers {Rota.Sim.Common.SimCredentials.MaxCouriers} or fewer.");
        }

        return options;
    }
}
