using System.Globalization;

namespace Rota.Sim.Merchant;

/// <summary>Command-line settings: <c>--orders 50 --merchants 3</c> and the rest below.</summary>
public sealed record MerchantOptions
{
    public int Orders { get; init; } = 50;

    /// <summary>How many different merchants take turns creating the orders.</summary>
    public int Merchants { get; init; } = 3;

    /// <summary>Orders' REST address.</summary>
    public Uri Url { get; init; } = new("http://localhost:5101");

    /// <summary>Seconds between two new orders (decimals allowed).</summary>
    public double IntervalSeconds { get; init; } = 0.5;

    /// <summary>JSON-lines file: one <c>{"orderId","code"}</c> per created order, read by the courier simulator.</summary>
    public string CodesFile { get; init; } = "codes.jsonl";

    /// <summary>Chance a merchant cancels the order a few seconds after creating it.</summary>
    public double CancelRate { get; init; } = 0.1;

    /// <summary>Same seed, same orders: a run can be repeated.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>How long to wait for every order to reach a final status before reporting the stuck ones.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Keycloak realm to get tokens from (e.g. http://localhost:8180/realms/rota). Without it the dev header is sent (Development only).</summary>
    public Uri? Auth { get; init; }

    /// <summary>Stop after creating the orders; do not wait for their outcome.</summary>
    public bool NoWait { get; init; }

    public static MerchantOptions Parse(string[] args)
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
            // A flag without a value (--no-wait) means "true".
            values[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        var options = new MerchantOptions();
        foreach (var (name, value) in values)
        {
            options = name switch
            {
                "orders" => options with { Orders = int.Parse(value, CultureInfo.InvariantCulture) },
                "merchants" => options with { Merchants = int.Parse(value, CultureInfo.InvariantCulture) },
                "url" => options with { Url = new Uri(value) },
                "auth" => options with { Auth = new Uri(value) },
                "interval" => options with { IntervalSeconds = double.Parse(value, CultureInfo.InvariantCulture) },
                "codes-file" => options with { CodesFile = value },
                "cancel-rate" => options with { CancelRate = double.Parse(value, CultureInfo.InvariantCulture) },
                "seed" => options with { Seed = int.Parse(value, CultureInfo.InvariantCulture) },
                "timeout" => options with { TimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture) },
                "no-wait" => options with { NoWait = bool.Parse(value) },
                _ => throw new ArgumentException($"Unknown option --{name}."),
            };
        }

        if (options.Orders < 1 || options.Merchants < 1 || options.IntervalSeconds < 0 || options.CancelRate is < 0 or > 1 || options.TimeoutSeconds < 1)
        {
            throw new ArgumentException("--orders and --merchants must be >= 1, --interval >= 0, --cancel-rate within [0, 1], --timeout >= 1.");
        }

        if (options.Auth is not null && options.Merchants > Rota.Sim.Common.SimCredentials.MaxMerchants)
        {
            throw new ArgumentException($"With --auth the dev realm has {Rota.Sim.Common.SimCredentials.MaxMerchants} simulated merchants; use --merchants {Rota.Sim.Common.SimCredentials.MaxMerchants} or fewer.");
        }

        return options;
    }
}
