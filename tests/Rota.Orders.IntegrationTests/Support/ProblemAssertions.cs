using System.Net;
using System.Text.Json;

namespace Rota.Orders.IntegrationTests.Support;

public static class ProblemAssertions
{
    /// <summary>
    /// Asserts status, <c>application/problem+json</c> and the catalog type
    /// (<c>https://rota.dev/errors/&lt;code&gt;</c>); returns the body for further checks.
    /// </summary>
    public static async Task<JsonElement> ShouldBeProblemAsync(
        this HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(status, text);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", text);

        using var document = JsonDocument.Parse(text);
        var problem = document.RootElement.Clone();
        problem.GetProperty("type").GetString().ShouldBe($"https://rota.dev/errors/{code}", text);
        problem.TryGetProperty("traceId", out _).ShouldBeTrue(text);
        return problem;
    }
}
