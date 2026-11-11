using System.Security.Cryptography;
using System.Text.Json;

namespace Rota.Orders.Api.Features.CreateOrder;

/// <summary>
/// Fingerprint of a request for idempotency: "same key, same request?". Computed from the parsed
/// request re-serialized in a fixed form, so whitespace, property order or "40.99030" vs "40.9903"
/// do not count as a different request; any changed value does.
/// </summary>
public static class RequestHash
{
    private static readonly JsonSerializerOptions _canonical = new(JsonSerializerDefaults.Web);

    public static string Compute<T>(T request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, _canonical)));
}
