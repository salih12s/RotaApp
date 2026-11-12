using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Rota.Auth.IntegrationTests;

/// <summary>A real service (or the gateway) in memory, configured only by the settings it is given.</summary>
public sealed class ServiceHost<TEntryPoint>(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>Builds requests that carry a bearer token.</summary>
public static class Bearer
{
    /// <summary>A request with an optional bearer token; the caller disposes the message.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string? token = null, string? json = "{}")
    {
        var message = new HttpRequestMessage(method, path);
        if (json is not null && method != HttpMethod.Get)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return message;
    }
}
