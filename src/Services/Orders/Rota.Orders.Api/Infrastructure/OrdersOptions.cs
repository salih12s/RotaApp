using System.ComponentModel.DataAnnotations;

namespace Rota.Orders.Api.Infrastructure;

/// <summary>
/// Orders settings, section <c>Orders</c>. The three keys are secrets: locally in
/// <c>dotnet user-secrets</c>, in containers as environment variables, never in the repository.
/// </summary>
public sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    /// <summary>Base64, at least 32 bytes: HMAC key for delivery code hashes.</summary>
    [Required]
    public string DeliveryCodeHashKey { get; set; } = string.Empty;

    /// <summary>Base64, exactly 32 bytes: AES-256-GCM key for secrets inside stored idempotent responses.</summary>
    [Required]
    public string ResponseEncryptionKey { get; set; } = string.Empty;

    /// <summary>PEM (PKCS#8) EC P-256 private key that signs tracking tokens (ADR-007).</summary>
    [Required]
    public string TrackingSigningKey { get; set; } = string.Empty;

    /// <summary>Public tracking page; the token is appended.</summary>
    [Required]
    public Uri TrackingBaseUrl { get; set; } = new("https://rota.local/track/");

    /// <summary>Not fixed by the docs; one day covers a same-day delivery with margin.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan TrackingTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    internal static bool HasValidKeys(OrdersOptions options) =>
        TryDecode(options.DeliveryCodeHashKey, out var hashKey) && hashKey.Length >= 32
        && TryDecode(options.ResponseEncryptionKey, out var encryptionKey) && encryptionKey.Length == 32;

    internal static byte[] Decode(string base64) => Convert.FromBase64String(base64);

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        bytes = new byte[base64.Length];
        if (Convert.TryFromBase64String(base64, bytes, out var written))
        {
            bytes = bytes[..written];
            return true;
        }

        return false;
    }
}
