using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rota.BuildingBlocks.Primitives;

namespace Rota.Notifications.Api.Domain;

/// <summary>Which order status becomes which webhook <c>type</c> (docs/05 §4). A status not listed here is not sent to merchants.</summary>
public static class WebhookEventTypes
{
    private static readonly FrozenDictionary<string, string> _types = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Assigned"] = "order.assigned",
        ["Unassigned"] = "order.unassigned",
        ["PickedUp"] = "order.picked_up",
        ["Delivered"] = "order.delivered",
        ["DeliveryFailed"] = "order.delivery_failed",
        ["Returned"] = "order.returned",
        ["Cancelled"] = "order.cancelled",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string? TypeFor(string toStatus) => _types.GetValueOrDefault(toStatus);
}

/// <summary>
/// <c>Rota-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c> where v1 = HMAC-SHA256(secret, "{t}.{raw body}") (docs/05 §4).
/// The timestamp is part of what is signed, so a captured request cannot be replayed with a fresh <c>t</c>.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "Rota-Signature";

    public static string Header(string secret, DateTimeOffset now, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);

        var t = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Compute(secret, t, body)}";
    }

    public static string Compute(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(mac);
    }
}

/// <summary>
/// 1 s, 5 s, 30 s, 2 min, 10 min, 30 min, then dead (docs/01 business rules). The first attempt is immediate, so a delivery
/// gets 7 attempts in all; the n-th failure schedules the n-th delay, and the 7th failure is the last.
/// </summary>
public sealed class BackoffSchedule
{
    public static BackoffSchedule Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)]);

    private readonly TimeSpan[] _delays;

    public BackoffSchedule(IReadOnlyList<TimeSpan> delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        _delays = [.. delays];
    }

    public int MaxAttempts => _delays.Length + 1;

    /// <summary>The wait after the <paramref name="failedAttempts"/>-th failed attempt, or <c>null</c> when it was the last one.</summary>
    public TimeSpan? DelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return failedAttempts <= _delays.Length ? _delays[failedAttempts - 1] : null;
    }
}

/// <summary>
/// The address rules behind SSRF protection (docs/06 §3): a merchant must not be able to point Rota at its own internal network.
/// The URL is checked when it is saved and the resolved address again at every connection (see <c>SafeConnect</c>), because a
/// name can resolve to a public address today and an internal one tomorrow (DNS rebinding).
/// </summary>
public static class WebhookUrlPolicy
{
    public const int MaxUrlLength = 500;

    public static Error InvalidUrl(string reason) => new("validation-failed", reason);

    /// <summary>
    /// The syntax rules: absolute https URL (http only where <paramref name="allowHttp"/>), no credentials, host not a forbidden literal IP
    /// (unless <paramref name="allowPrivateNetworks"/>: development and tests, where the receiver runs on this machine).
    /// </summary>
    public static Result<Uri> Validate(string? candidate, bool allowHttp, bool allowPrivateNetworks = false)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxUrlLength || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return InvalidUrl($"url must be an absolute URL of at most {MaxUrlLength} characters.");
        }

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        if (!secure && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return InvalidUrl("url must use https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return InvalidUrl("url must not contain credentials.");
        }

        if (!allowPrivateNetworks && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && IsForbidden(literal))
        {
            return InvalidUrl("url must not point to a private or internal address.");
        }

        return uri;
    }

    /// <summary>Loopback, private, link-local, unspecified, multicast and the IPv6 equivalents (including IPv4-mapped ones).</summary>
    public static bool IsForbidden(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10 link-local, fc00::/7 unique local, ff00::/8 multicast.
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10                                    // 10.0.0.0/8
            || (b[0] == 172 && b[1] is >= 16 and <= 31)      // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16 (cloud metadata lives here)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)     // 100.64.0.0/10 carrier-grade NAT
            || b[0] >= 224;                                  // multicast and reserved
    }
}

/// <summary>
/// Stops sending to a merchant that keeps failing: after <see cref="Threshold"/> failures in a row the circuit opens for
/// <see cref="OpenFor"/> and no request leaves for that merchant (docs/06 §6). Pure: the caller passes the time.
/// </summary>
public sealed class MerchantCircuit(int threshold = 5, TimeSpan? openFor = null)
{
    public int Threshold { get; } = threshold;

    public TimeSpan OpenFor { get; } = openFor ?? TimeSpan.FromMinutes(1);

    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public bool IsOpen(DateTimeOffset now) => _openUntil is { } until && now < until;

    /// <summary>When a request may go out again; <c>null</c> while closed.</summary>
    public DateTimeOffset? OpenUntil(DateTimeOffset now) => IsOpen(now) ? _openUntil : null;

    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _openUntil = null;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= Threshold)
        {
            // Half-open after the pause: one more failure re-opens it at once (the counter stays at the threshold).
            _openUntil = now + OpenFor;
        }
    }
}
