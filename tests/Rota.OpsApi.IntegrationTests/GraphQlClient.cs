using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// The real Ops API in memory on a given database. Environment stays Development (DevAuth, ADR-006): the caller is named in a header.
/// <paramref name="settings"/> are extra configuration keys (<c>Ops:DispatchTimeout</c> and so on); <paramref name="redis"/> is needed only by tests that use live subscriptions.
/// </summary>
public sealed class OpsApiFactory(
    string connectionString,
    string? trackingAddress = null,
    Action<IServiceCollection>? configureServices = null,
    string? redis = null,
    IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ops", connectionString);
        builder.UseSetting("Messaging:Enabled", "false");

        // A call to Tracking must not fail the test because of a cold gRPC channel; the 300 ms rule has its own test (it sets the key itself).
        builder.UseSetting("Ops:TrackingDeadline", "00:00:10");
        if (trackingAddress is not null)
        {
            builder.UseSetting("Ops:TrackingAddress", trackingAddress);
        }

        if (redis is not null)
        {
            builder.UseSetting("ConnectionStrings:Redis", redis);
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => configureServices?.Invoke(services));
    }

    /// <summary>Sends one GraphQL request as <paramref name="devUser"/> (null = anonymous) and returns the parsed JSON body.</summary>
    public async Task<JsonElement> QueryAsync(string query, object? variables, string? devUser, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        if (devUser is not null)
        {
            request.Headers.Add(DevUsers.HeaderName, devUser);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public static string FirstErrorCode(JsonElement body) =>
        body.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString()!;
}
