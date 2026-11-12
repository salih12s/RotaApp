namespace Rota.Gateway.RateLimiting;

/// <summary>The <c>RateLimit</c> configuration section (docs/06 §3).</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Checked in order; the first rule whose method and path match decides. A request no rule matches is not limited.</summary>
    public List<RateLimitRule> Rules { get; set; } = [];
}

public sealed class RateLimitRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Empty = any method.</summary>
    public string? Method { get; set; }

    /// <summary>The request path must start with this (case-insensitive).</summary>
    public string PathPrefix { get; set; } = "/";

    /// <summary>The bucket size: how many requests may arrive in one burst.</summary>
    public int Limit { get; set; }

    /// <summary>The bucket refills <see cref="Limit"/> tokens over this many seconds (50 per second = Limit 50, PerSeconds 1).</summary>
    public double PerSeconds { get; set; } = 1;

    /// <summary>Whose bucket: <c>merchant</c> (merchant_id claim), <c>subject</c> (sub claim) or <c>ip</c>. A token that lacks the claim falls back to the IP.</summary>
    public string Key { get; set; } = "subject";
}
