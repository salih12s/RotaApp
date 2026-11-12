namespace Rota.Notifications.Api.Infrastructure;

/// <summary>Settings, section <c>Notifications</c>.</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Where ASP.NET Data Protection keeps its keys. It must survive restarts (and be shared by every instance): the keys encrypt
    /// the webhook secrets in the database, and a lost key means every stored secret is unreadable.
    /// </summary>
    public string KeyDirectory { get; init; } = "data-protection-keys";

    /// <summary>http webhook URLs are accepted. Development and tests only (docs/06 §3).</summary>
    public bool AllowHttp { get; init; }

    /// <summary>Private and loopback addresses are accepted. Development and tests only: the simulator's receiver runs on this machine.</summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>A delivery attempt that takes longer than this has failed (docs/06 §6).</summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Switch for tests that drive delivery rounds by hand. Read at runtime.</summary>
    public bool WorkerEnabled { get; init; } = true;

    /// <summary>Infrastructure timer: real time, never the business <see cref="TimeProvider"/> (docs/07 §3).</summary>
    public TimeSpan WorkerPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int WorkerBatchSize { get; init; } = 100;

    /// <summary>Most rows one round takes for one merchant, so a merchant with a huge backlog cannot fill the whole batch.</summary>
    public int PerMerchantClaim { get; init; } = 8;

    /// <summary>Requests in flight to one merchant at the same time (bulkhead, docs/06 §6).</summary>
    public int MerchantConcurrency { get; init; } = 4;

    /// <summary>A claimed row is hidden from other workers this long; it must be longer than <see cref="DeliveryTimeout"/>.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(30);

    public int CircuitThreshold { get; init; } = 5;

    public TimeSpan CircuitOpenFor { get; init; } = TimeSpan.FromMinutes(1);
}
