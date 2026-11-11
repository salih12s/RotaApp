using System.Security.Cryptography;
using System.Text;

namespace Rota.Orders.Api.Infrastructure;

/// <summary>
/// AES-256-GCM for secrets that must be stored but must not be readable from a database leak
/// (the delivery code inside a stored idempotent response). GCM also detects tampering: a
/// modified ciphertext fails to decrypt instead of decrypting to garbage.
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("The response encryption key must be 32 bytes (AES-256).", nameof(key));
        }

        _key = [.. key];
    }

    /// <summary>
    /// <paramref name="associatedData"/> (e.g. the order id) is authenticated but not encrypted:
    /// a ciphertext copied onto another row fails to decrypt there.
    /// </summary>
    public string Protect(string plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(associatedData);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce); // never reuse a nonce with the same key

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length), Encoding.UTF8.GetBytes(associatedData));
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedText, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        ArgumentNullException.ThrowIfNull(associatedData);

        var input = Convert.FromBase64String(protectedText);
        var cipherLength = input.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, cipherLength), input.AsSpan(NonceSize + cipherLength), plain, Encoding.UTF8.GetBytes(associatedData));
        return Encoding.UTF8.GetString(plain);
    }
}
