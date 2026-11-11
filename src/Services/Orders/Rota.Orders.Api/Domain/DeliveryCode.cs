using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rota.BuildingBlocks.Primitives;

namespace Rota.Orders.Api.Domain;

/// <summary>
/// The 4-digit proof-of-delivery code, stored only as a keyed hash (invariant I-5).
/// Part of the <see cref="Order"/> aggregate; only the order changes it.
/// </summary>
public sealed class DeliveryCode
{
    public const int Length = 4;
    public const int MaxFailedAttempts = 5;

    private DeliveryCode(string hash) => Hash = hash;

    public string Hash { get; }

    public int FailedAttempts { get; private set; }

    public bool IsLocked { get; private set; }

    public int RemainingAttempts => MaxFailedAttempts - FailedAttempts;

    /// <summary>Creates a new code. The plain text is returned once (to the merchant) and never stored.</summary>
    public static (DeliveryCode Code, string PlainText) Generate(DeliveryCodeHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        // CSPRNG, uniform over 0000–9999 (no modulo bias); Random would be predictable.
        var plainText = RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4", CultureInfo.InvariantCulture);
        return (new DeliveryCode(hasher.Hash(plainText)), plainText);
    }

    public static bool IsWellFormed(string? candidate) =>
        candidate is { Length: Length } && candidate.All(char.IsAsciiDigit);

    /// <summary>Checks a candidate. A wrong (well-formed) code counts as an attempt; the 5th wrong one locks.</summary>
    internal Result Verify(string candidate, DeliveryCodeHasher hasher)
    {
        if (IsLocked)
        {
            return OrderErrors.DeliveryCodeLocked;
        }

        // A typo like "48a1" is an input error, not a guess: it does not consume an attempt.
        if (!IsWellFormed(candidate))
        {
            return OrderErrors.MalformedDeliveryCode;
        }

        if (hasher.Matches(candidate, Hash))
        {
            return Result.Success();
        }

        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
        {
            IsLocked = true;
        }

        return OrderErrors.InvalidDeliveryCode;
    }

    /// <summary>Dispatcher unlock: clears the lock and gives back all attempts.</summary>
    internal void Unlock()
    {
        FailedAttempts = 0;
        IsLocked = false;
    }
}

/// <summary>
/// HMAC-SHA256 with a server-side secret. A plain SHA-256 of 10,000 possible codes is brute-forced
/// offline in milliseconds; without the key a leaked hash reveals nothing.
/// </summary>
public sealed class DeliveryCodeHasher
{
    public const int MinKeyBytes = 32;

    private readonly byte[] _key;

    public DeliveryCodeHasher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinKeyBytes)
        {
            throw new ArgumentException($"The delivery code key must be at least {MinKeyBytes} bytes.", nameof(key));
        }

        _key = [.. key];
    }

    public string Hash(string code) => Convert.ToHexStringLower(Compute(code));

    public bool Matches(string code, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);

        // Constant-time comparison: response time must not leak how many bytes matched.
        return CryptographicOperations.FixedTimeEquals(Compute(code), Convert.FromHexString(expectedHash));
    }

    private byte[] Compute(string code) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(code));
}
