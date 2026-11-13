using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using StackExchange.Redis;

namespace Rota.OpsApi.Infrastructure;

/// <summary>A courier's new position, as Tracking publishes it on <c>trk:courier:{id}:location</c>.</summary>
public sealed record CourierLocationUpdate(Guid CourierId, double Lat, double Lon, DateTimeOffset RecordedAt);

/// <summary>
/// This instance's live view of every courier position. It subscribes <b>once</b> to Tracking's Redis channels (a pattern covers all couriers) and hands each
/// message to the WebSocket subscribers of this process (docs/05 §5: every instance listens to Tracking itself, so no instance depends on another).
/// Each subscriber has a small buffer that drops the oldest position when it is full: a slow client must see the newest point, not delay everyone else.
/// </summary>
public sealed partial class CourierLocationFeed(IConnectionMultiplexer redis, ILogger<CourierLocationFeed> logger) : IDisposable
{
    private const int BufferSize = 64;

    private static readonly RedisChannel _pattern = RedisChannel.Pattern("trk:courier:*:location");
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Listener> _listeners = [];
    private readonly SemaphoreSlim _start = new(1, 1);
    private bool _started;

    /// <summary>Positions of the couriers <paramref name="wanted"/> accepts, until the caller stops listening.</summary>
    public async IAsyncEnumerable<CourierLocationUpdate> SubscribeAsync(Func<Guid, bool> wanted, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        await EnsureStartedAsync();

        var channel = Channel.CreateBounded<CourierLocationUpdate>(
            new BoundedChannelOptions(BufferSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _listeners[id] = new Listener(wanted, channel.Writer);
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            _listeners.TryRemove(id, out _);
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }

        await _start.WaitAsync();
        try
        {
            if (!_started)
            {
                // If Redis is unreachable this throws and the next subscriber tries again; the connection itself keeps reconnecting.
                await redis.GetSubscriber().SubscribeAsync(_pattern, (_, message) => OnMessage(message));
                _started = true;
            }
        }
        finally
        {
            _start.Release();
        }
    }

    private void OnMessage(RedisValue message)
    {
        CourierLocationUpdate? update;
        try
        {
            update = JsonSerializer.Deserialize<CourierLocationUpdate>(message.ToString(), _json);
        }
        catch (JsonException)
        {
            LogMalformed(logger);
            return;
        }

        if (update is null)
        {
            return;
        }

        foreach (var listener in _listeners.Values)
        {
            if (listener.Wanted(update.CourierId))
            {
                listener.Writer.TryWrite(update);
            }
        }
    }

    public void Dispose() => _start.Dispose();

    private sealed record Listener(Func<Guid, bool> Wanted, ChannelWriter<CourierLocationUpdate> Writer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A malformed message arrived on a Tracking location channel and was skipped")]
    private static partial void LogMalformed(ILogger logger);
}
