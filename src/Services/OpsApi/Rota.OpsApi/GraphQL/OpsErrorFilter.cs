using HotChocolate.Execution;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.GraphQL;

/// <summary>A subscription that must not start. Carries the error code the client sees (<c>invalid-tracking-token</c>, <c>AUTH_NOT_AUTHORIZED</c>, ...).</summary>
public sealed class SubscriptionRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Turns our own exceptions into clean, coded GraphQL errors instead of Hot Chocolate's generic "Unexpected Execution Error" (which in Development
/// also carries a stack trace): an outage of Dispatch becomes the catalog's <c>dependency-unavailable</c> (docs/06 §5), a refused subscription keeps its
/// code. Every other unexpected exception keeps the default treatment (message hidden outside Development).
/// </summary>
public sealed class OpsErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Exception switch
        {
            DispatchUnavailableException outage => Coded(error, outage.Message, "dependency-unavailable"),
            SubscriptionRefusedException refused => Coded(error, refused.Message, refused.Code),
            _ => error,
        };
    }

    private static IError Coded(IError error, string message, string code) =>
        ErrorBuilder.FromError(error).SetMessage(message).SetCode(code).SetException(null).Build();
}
