using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>Settings, section <c>Dispatch</c>. Business rules (offer TTL, attempts) live in <see cref="Rules"/>.</summary>
public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>Tracking's gRPC address (HTTP/2). Internal network only (docs/06 §1).</summary>
    public string TrackingAddress { get; init; } = "http://localhost:5203";

    public int SearchRadiusMeters { get; init; } = 5_000;

    public int CandidateLimit { get; init; } = 20;

    /// <summary>Whole-call deadline for <c>GetNearbyCouriers</c>, retry included (docs/05 §2: 200 ms).</summary>
    public TimeSpan NearbyDeadline { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Switch for tests that drive the search and timeout steps by hand. Read at runtime, so a test
    /// factory can switch it off after registration.
    /// </summary>
    public bool WorkersEnabled { get; init; } = true;

    /// <summary>Infrastructure timers: real time, never the business <see cref="TimeProvider"/> (docs/07 §3).</summary>
    public TimeSpan WorkerPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int WorkerBatchSize { get; init; } = 20;

    /// <summary>
    /// A job picked up for searching is hidden from other workers this long (<c>next_search_at</c> is pushed
    /// forward). If the worker dies, the job becomes due again by itself.
    /// </summary>
    public TimeSpan SearchLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Idle-connection keep-alive on the offer stream (docs/05 §2: 15 s).</summary>
    public TimeSpan StreamHeartbeat { get; init; } = TimeSpan.FromSeconds(15);

    public DispatchRules Rules { get; init; } = DispatchRules.Default;
}
