using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;

namespace Rota.BuildingBlocks.Web.Errors;

/// <summary>
/// The HTTP side of the error catalog (docs/06-security-and-errors.md §5): code → status and title.
/// Domain errors only carry a code; this table is the single place that turns it into HTTP.
/// </summary>
public static class ErrorCatalog
{
    public const string TypeBaseUri = "https://rota.dev/errors/";

    public const string ValidationFailed = "validation-failed";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string ConcurrencyConflict = "concurrency-conflict";
    public const string DependencyUnavailable = "dependency-unavailable";
    public const string UnexpectedError = "unexpected-error";

    private static readonly FrozenDictionary<string, (int Status, string Title)> _entries =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            [ValidationFailed] = (StatusCodes.Status400BadRequest, "One or more fields are invalid."),
            ["idempotency-key-missing"] = (StatusCodes.Status400BadRequest, "The Idempotency-Key header is required."),
            ["invalid-delivery-code"] = (StatusCodes.Status400BadRequest, "The delivery code is wrong."),
            [Unauthorized] = (StatusCodes.Status401Unauthorized, "Authentication is required."),
            [Forbidden] = (StatusCodes.Status403Forbidden, "Your role does not allow this operation."),
            [NotFound] = (StatusCodes.Status404NotFound, "The resource was not found."),
            ["invalid-order-transition"] = (StatusCodes.Status409Conflict, "The order's status does not allow this operation."),
            ["invalid-courier-transition"] = (StatusCodes.Status409Conflict, "The courier's state does not allow this operation."),
            ["idempotency-key-reused"] = (StatusCodes.Status409Conflict, "The Idempotency-Key was already used with a different request."),
            ["duplicate-merchant-order-ref"] = (StatusCodes.Status409Conflict, "An order with this merchantOrderRef already exists."),
            [ConcurrencyConflict] = (StatusCodes.Status409Conflict, "The resource was changed by another request; reload and retry."),
            ["offer-not-pending"] = (StatusCodes.Status409Conflict, "The offer is no longer pending."),
            ["courier-busy"] = (StatusCodes.Status409Conflict, "The courier is on a delivery."),
            ["courier-not-available"] = (StatusCodes.Status409Conflict, "The courier is not available."),
            ["invalid-delivery-state"] = (StatusCodes.Status409Conflict, "The webhook delivery is not in a state that allows this operation."),
            ["job-not-assignable"] = (StatusCodes.Status409Conflict, "The order cannot be assigned in its current state."),
            ["delivery-code-locked"] = (StatusCodes.Status409Conflict, "The delivery code is locked after too many wrong attempts."),
            ["out-of-service-area"] = (StatusCodes.Status422UnprocessableEntity, "Pickup and dropoff are too far apart."),
            ["rate-limited"] = (StatusCodes.Status429TooManyRequests, "Too many requests."),
            [DependencyUnavailable] = (StatusCodes.Status503ServiceUnavailable, "A required dependency is unavailable."),
            [UnexpectedError] = (StatusCodes.Status500InternalServerError, "An unexpected error occurred."),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Status and title for a code. An unknown code (including internal ones such as
    /// <c>invalid-job-transition</c>) reaching HTTP is a bug, so it maps to 500.
    /// </summary>
    public static (int Status, string Title) Lookup(string code) =>
        _entries.TryGetValue(code, out var entry) ? entry : _entries[UnexpectedError];

    public static string TypeUri(string code) => TypeBaseUri + code;

    /// <summary>Catalog code for a bare status code (401 from the auth middleware, 404 for an unknown route…).</summary>
    public static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ValidationFailed,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => ConcurrencyConflict,
        StatusCodes.Status503ServiceUnavailable => DependencyUnavailable,
        _ => UnexpectedError,
    };
}
