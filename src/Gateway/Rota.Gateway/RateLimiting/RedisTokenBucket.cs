using StackExchange.Redis;

namespace Rota.Gateway.RateLimiting;

/// <summary>
/// A token bucket whose state lives in Redis, so every gateway instance spends from the same bucket (docs/06 §3).
/// The whole read-refill-spend step is one Lua script: Redis runs it without interleaving, so two instances cannot
/// both take the last token. The script reads Redis' own clock (<c>TIME</c>): the instances' clocks never enter the maths.
/// </summary>
public sealed class RedisTokenBucket(IConnectionMultiplexer redis)
{
    // @bucket = the bucket key, @capacity = bucket size, @rate = refill tokens per second.
    // Returns {allowed (1/0), milliseconds until a token is available}.
    private const string Script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local capacity = tonumber(@capacity)
        local rate = tonumber(@rate)
        local state = redis.call('HMGET', @bucket, 'tokens', 'ts')
        local tokens = tonumber(state[1])
        local ts = tonumber(state[2])
        if tokens == nil then
          tokens = capacity
          ts = now
        end
        tokens = math.min(capacity, tokens + math.max(0, now - ts) * rate / 1000)
        local allowed = 0
        local retry = 0
        if tokens >= 1 then
          tokens = tokens - 1
          allowed = 1
        else
          retry = math.ceil((1 - tokens) * 1000 / rate)
        end
        redis.call('HSET', @bucket, 'tokens', tostring(tokens), 'ts', tostring(now))
        redis.call('PEXPIRE', @bucket, math.ceil(capacity * 1000 / rate) + 1000)
        return {allowed, retry}
        """;

    private static readonly LuaScript Compiled = LuaScript.Prepare(Script);

    public async Task<(bool Allowed, TimeSpan RetryAfter)> TryTakeAsync(string bucketKey, int capacity, double refillPerSecond)
    {
        var db = redis.GetDatabase();
        var result = (RedisResult[]?)await db.ScriptEvaluateAsync(Compiled, new
        {
            bucket = (RedisKey)bucketKey,
            capacity,
            rate = refillPerSecond,
        });

        return ((int)result![0] == 1, TimeSpan.FromMilliseconds((long)result[1]));
    }
}
