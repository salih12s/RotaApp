using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rota.Dispatch.IntegrationTests.Support;

public static class ProblemAssertions
{
    /// <summary>Checks the status and the catalog code in the ProblemDetails <c>type</c> (docs/06 §5); returns the body for extra checks.</summary>
    public static async Task<JsonElement> ShouldBeProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().ShouldBe($"https://rota.dev/errors/{code}");
        return body;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
