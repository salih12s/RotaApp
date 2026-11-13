namespace Rota.OpsApi.Infrastructure;

/// <summary>Ops API settings, section <c>Ops</c>: where the other services are and how long to wait for them (docs/06 §6).</summary>
public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    /// <summary>Tracking's gRPC port (<c>GetLastLocations</c>).</summary>
    public Uri TrackingAddress { get; set; } = new("http://localhost:5203");

    /// <summary>Dispatch's REST port (manual assignment, restart).</summary>
    public Uri DispatchAddress { get; set; } = new("http://localhost:5102");

    /// <summary>Orders' REST port: only <c>/.well-known/tracking-jwks</c> is read from it.</summary>
    public Uri OrdersAddress { get; set; } = new("http://localhost:5101");

    /// <summary>docs/06 §6: 300 ms; past that the panel gets its answer without positions.</summary>
    public TimeSpan TrackingDeadline { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>docs/06 §6: 2 s. A command is never retried, so this is the whole wait.</summary>
    public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The breakers open when half of the calls in 30 s failed, but only after this many calls (a single early failure must not open it).</summary>
    public int BreakerMinimumThroughput { get; set; } = 5;

    public TimeSpan BreakerDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long fetched tracking public keys are trusted before they are fetched again.</summary>
    public TimeSpan JwksCacheLifetime { get; set; } = TimeSpan.FromMinutes(10);
}
