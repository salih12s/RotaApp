using System.Globalization;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Primitives;
using Rota.BuildingBlocks.Web.Auth;
using Rota.BuildingBlocks.Web.Errors;

namespace Rota.Gateway.RateLimiting;

/// <summary>
/// Answers 429 with <c>Retry-After</c> when a caller has spent its bucket. Runs after authentication, so the bucket belongs to the
/// token's <c>merchant_id</c> / <c>sub</c>, not to whatever the caller claims. If Redis is down the request is let through
/// (fail open): the limiter protects capacity, it is not the security boundary; the token was already checked.
/// </summary>
public sealed partial class RateLimitMiddleware(
    RequestDelegate next,
    RedisTokenBucket bucket,
    IOptions<RateLimitOptions> options,
    ILogger<RateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var rule = options.Value.Rules.FirstOrDefault(r => Matches(r, context.Request));
        if (rule is null)
        {
            await next(context);
            return;
        }

        bool allowed;
        TimeSpan retryAfter;
        try
        {
            (allowed, retryAfter) = await bucket.TryTakeAsync(
                $"rl:{rule.Name}:{PartitionOf(rule, context)}", rule.Limit, rule.Limit / rule.PerSeconds);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException)
        {
            LogStoreUnavailable(logger, ex, rule.Name);
            await next(context);
            return;
        }

        if (allowed)
        {
            await next(context);
            return;
        }

        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await new Error(ErrorCatalog.RateLimited, $"Rate limit '{rule.Name}' exceeded.").ToProblem().ExecuteAsync(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit store unavailable; letting the request through (rule {Rule}).")]
    private static partial void LogStoreUnavailable(ILogger logger, Exception exception, string rule);

    private static bool Matches(RateLimitRule rule, HttpRequest request) =>
        (string.IsNullOrEmpty(rule.Method) || string.Equals(rule.Method, request.Method, StringComparison.OrdinalIgnoreCase))
        && request.Path.StartsWithSegments(rule.PathPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string PartitionOf(RateLimitRule rule, HttpContext context)
    {
        var user = context.User;
        var key = rule.Key switch
        {
            "merchant" => user.GetMerchantId(),
            "subject" => user.GetSubject(),
            _ => null,
        };

        return key ?? $"ip:{context.Connection.RemoteIpAddress}";
    }
}
