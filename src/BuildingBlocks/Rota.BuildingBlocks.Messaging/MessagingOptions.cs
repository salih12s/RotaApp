namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// Settings, section <c>Messaging</c>. The broker address is the connection string <c>RabbitMq</c>
/// (an <c>amqp://</c> URI, a secret outside local development).
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";
    public const string ConnectionName = "RabbitMq";

    /// <summary>
    /// Switch for tests and tools that run a service without a broker. Read at runtime, not at
    /// registration, so configuration added late (WebApplicationFactory) still counts.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the relay sleeps when the outbox is empty.</summary>
    public TimeSpan RelayPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Rows locked and published per round. Small enough to hold the lock only briefly.</summary>
    public int RelayBatchSize { get; set; } = 50;

    /// <summary>Processed outbox rows older than this are deleted (docs/04: 7 days).</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Unacknowledged messages a consumer may hold at once.</summary>
    public ushort ConsumerPrefetch { get; set; } = 10;

    /// <summary>
    /// One entry per retry: a failed message waits in <c>&lt;queue&gt;.retry.N</c> for that long, then
    /// returns to the queue. After the last delay fails again it goes to <c>&lt;queue&gt;.dlq</c>
    /// (3 retries = 4 handler runs). Changing a delay for an existing broker needs the old retry
    /// queues deleted: RabbitMQ refuses to redeclare a queue with different arguments.
    /// </summary>
    /// <remarks>
    /// Null = defaults. Not initialised here on purpose: the configuration binder appends to an existing
    /// array instead of replacing it, so a configured value would be glued onto the defaults.
    /// </remarks>
    public TimeSpan[]? RetryDelays { get; set; }

    public static IReadOnlyList<TimeSpan> DefaultRetryDelays { get; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public IReadOnlyList<TimeSpan> EffectiveRetryDelays => RetryDelays is { Length: > 0 } ? RetryDelays : DefaultRetryDelays;

    /// <summary>Pause before reconnecting after a broker or database failure.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
}
