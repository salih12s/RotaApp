using Microsoft.AspNetCore.Http;
using Rota.BuildingBlocks.Primitives;

namespace Rota.BuildingBlocks.Web.Errors;

public static class ProblemResults
{
    /// <summary>Turns an expected business error into an RFC 9457 ProblemDetails response.</summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (status, title) = ErrorCatalog.Lookup(error.Code);
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            title: title,
            type: ErrorCatalog.TypeUri(error.Code),
            extensions: error.Details);
    }

    /// <summary>404 for "does not exist" and "not yours" alike, so callers cannot probe for other merchants' ids.</summary>
    public static IResult NotFound() => new Error(ErrorCatalog.NotFound, "The resource was not found.").ToProblem();
}
