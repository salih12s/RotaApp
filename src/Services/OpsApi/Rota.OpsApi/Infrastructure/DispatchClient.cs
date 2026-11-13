using System.Net;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Rota.OpsApi.Infrastructure;

/// <summary>What Dispatch answers for a job after a command.</summary>
public sealed record DispatchJob(Guid OrderId, string Status, Guid? CourierId);

// The three outcomes a dispatcher can cause and fix (docs/05 §5). Each one becomes a typed error in the mutation payload, not a GraphQL error.
public sealed class CourierNotAvailableException(string message) : Exception(message);

public sealed class JobNotAssignableException(string message) : Exception(message);

public sealed class OrderNotFoundException(string message) : Exception(message);

/// <summary>Dispatch did not answer in time, is down, or its circuit is open. An outage, not a business outcome, so it stays a plain GraphQL error.</summary>
public sealed class DispatchUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Ops API → Dispatch commands (docs/06 §6): 2 s timeout, circuit breaker, <b>no retry</b>: assigning twice is not harmless, so a failure goes back to
/// the dispatcher, who decides. The caller's own token is forwarded (<see cref="CallerIdentityHandler"/>): Dispatch checks the <c>dispatcher</c> role itself.
/// </summary>
public sealed class DispatchClient(HttpClient http)
{
    private const string ProblemTypeBase = "https://rota.dev/errors/";

    public Task<DispatchJob> AssignCourierAsync(Guid orderId, Guid courierId, CancellationToken cancellationToken) =>
        SendAsync(orderId, "manual-assign", JsonContent.Create(new { courierId }), cancellationToken);

    public Task<DispatchJob> RestartAsync(Guid orderId, CancellationToken cancellationToken) =>
        SendAsync(orderId, "restart", content: null, cancellationToken);

    private async Task<DispatchJob> SendAsync(Guid orderId, string action, HttpContent? content, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri($"/api/v1/dispatch-jobs/{orderId:D}/{action}", UriKind.Relative), content, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException)
        {
            throw new DispatchUnavailableException("Dispatch did not answer; try again in a moment.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<DispatchJob>(JsonSerializerOptions.Web, cancellationToken)
                    ?? throw new DispatchUnavailableException("Dispatch sent an empty answer.");
            }

            throw await ToExceptionAsync(response, cancellationToken);
        }
    }

    private static async Task<Exception> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // 5xx and 429 are the dependency's trouble (they also feed the circuit breaker); 4xx is Dispatch explaining a refusal.
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new DispatchUnavailableException($"Dispatch answered {(int)response.StatusCode}.");
        }

        var problem = await ReadProblemAsync(response, cancellationToken);
        var detail = problem.Detail ?? "Dispatch refused the request.";
        var code = problem.Type is { } type && type.StartsWith(ProblemTypeBase, StringComparison.Ordinal) ? type[ProblemTypeBase.Length..] : null;

        return code switch
        {
            "courier-not-available" or "courier-busy" => new CourierNotAvailableException(detail),
            "job-not-assignable" => new JobNotAssignableException(detail),
            // Dispatch answers not-found for a missing courier too ("The courier was not found."); for the dispatcher that is a courier who cannot take the job.
            "not-found" when detail.Contains("courier", StringComparison.Ordinal) => new CourierNotAvailableException(detail),
            "not-found" => new OrderNotFoundException(detail),
            // 400/401/403: our own request or forwarded token is wrong. That is a bug or a setup problem, not something the dispatcher can fix.
            _ => new InvalidOperationException($"Dispatch refused the request with {(int)response.StatusCode} ({code ?? "no code"}): {detail}"),
        };
    }

    private static async Task<ProblemBody> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemBody>(JsonSerializerOptions.Web, cancellationToken) ?? new ProblemBody(null, null);
        }
        catch (JsonException)
        {
            return new ProblemBody(null, null);
        }
    }

    private sealed record ProblemBody(string? Type, string? Detail);
}

/// <summary>Sends the caller's own credentials along to Dispatch: its <c>Authorization</c> header, or in Development the <c>X-Dev-User</c> header (ADR-006).</summary>
public sealed class CallerIdentityHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    private static readonly string[] _forwarded = ["Authorization", "X-Dev-User"];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (accessor.HttpContext is { } context)
        {
            foreach (var name in _forwarded)
            {
                if (context.Request.Headers.TryGetValue(name, out var values))
                {
                    request.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values!);
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
