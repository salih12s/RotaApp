using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// A real HTTP server on a loopback port that answers 200 to anything and remembers what the gateway forwarded.
/// It stands in for a service so the gateway tests do not wait for refused connections (slow on Windows).
/// </summary>
public sealed class FakeBackend : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeBackend(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public string? LastTraceparent { get; private set; }

    public string? LastAuthorization { get; private set; }

    public static async Task<FakeBackend> StartAsync()
    {
        // An empty content root: the test output folder holds copies of the services' appsettings.json (HTTP/2-only gRPC endpoints among them).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Directory.CreateTempSubdirectory("rota-fake-backend-").FullName });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeBackend? self = null;
        app.MapFallback(context =>
        {
            self!.LastTraceparent = context.Request.Headers["traceparent"].ToString();
            self.LastAuthorization = context.Request.Headers.Authorization.ToString();
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await app.StartAsync();
        self = new FakeBackend(app, app.Urls.First());
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
