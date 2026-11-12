using StackExchange.Redis;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>
/// "Something happened to this courier's offer" over Redis Pub/Sub, so the Dispatch instance that holds
/// the courier's stream (any instance) can wake up. The message carries only the offer id: the stream reads
/// the truth from PostgreSQL, so a late, duplicated or lost push can never show a wrong offer.
/// </summary>
public sealed partial class OfferNotifier(IConnectionMultiplexer redis, ILogger<OfferNotifier> logger)
{
    public static RedisChannel ChannelFor(Guid courierId) =>
        RedisChannel.Literal($"dsp:courier:{courierId:D}:offers");

    /// <summary>
    /// Best effort: a failure is logged, not thrown, and the call never waits for Redis (<c>FireAndForget</c>), so a dead Redis
    /// cannot slow down a commit. The offer is already committed; the stream's poll finds it.
    /// </summary>
    public async Task NotifyAsync(Guid courierId, Guid offerId)
    {
        try
        {
            await redis.GetSubscriber().PublishAsync(ChannelFor(courierId), offerId.ToString("D"), CommandFlags.FireAndForget);
        }
        catch (RedisException ex)
        {
            LogPublishFailed(logger, ex, courierId, offerId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not push offer {OfferId} to courier {CourierId}; the stream will pick it up on its next poll")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid courierId, Guid offerId);
}
