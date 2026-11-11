namespace Rota.Orders.Api.Infrastructure;

/// <summary>
/// A processed <c>Idempotency-Key</c>: the stored response is returned again for a retry.
/// The primary key (merchant_id, key) is the guard; a "SELECT first, then INSERT" check is not.
/// </summary>
public sealed class IdempotencyKey
{
    public required string MerchantId { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of the canonical request: same key with a different body → 409.</summary>
    public required string RequestHash { get; init; }

    public required int ResponseStatus { get; init; }

    /// <summary>The response as JSON (jsonb). Secrets inside it are encrypted, never plain.</summary>
    public required string ResponseBody { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
