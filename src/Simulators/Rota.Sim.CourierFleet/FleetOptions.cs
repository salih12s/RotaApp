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
                "duration" => options with { DurationSeconds = int.Parse(value, CultureInfo.InvariantCulture) },
                "disconnect-rate" => options with { DisconnectRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "reorder-rate" => options with { ReorderRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "seed" => options with { Seed = int.Parse(value, CultureInfo.InvariantCulture) },
                "probe-nearby" => options with { ProbeNearby = bool.Parse(value) },
                _ => throw new ArgumentException($"Unknown option --{name}."),
            };
        }

        if (options.Couriers < 1 || options.IntervalSeconds <= 0 || options.DisconnectRate is < 0 or > 1 || options.ReorderRate is < 0 or > 1)
        {
            throw new ArgumentException("--couriers must be >= 1, --interval > 0, and the rates within [0, 1].");
        }

        return options;
    }
}
