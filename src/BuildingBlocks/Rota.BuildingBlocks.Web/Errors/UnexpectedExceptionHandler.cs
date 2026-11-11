using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Rota.BuildingBlocks.Web.Errors;

/// <summary>
/// Last line of defence: an exception that nobody expected becomes a 500 ProblemDetails with a
/// traceId. The stack trace goes to the log only, never to the client. A transient database
/// failure (connection refused, timeout) is 503 instead: the client may retry later.
/// </summary>
public sealed partial class UnexpectedExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<UnexpectedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        LogUnexpected(logger, exception, httpContext.Request.Method, httpContext.Request.Path);

        var code = IsTransientDatabaseFailure(exception) ? ErrorCatalog.DependencyUnavailable : ErrorCatalog.UnexpectedError;
        var (status, title) = ErrorCatalog.Lookup(code);
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Type = ErrorCatalog.TypeUri(code),
            },
        });
    }

    // EF wraps provider errors (DbUpdateException → NpgsqlException), so walk the chain.
    private static bool IsTransientDatabaseFailure(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string method, string path);
}
