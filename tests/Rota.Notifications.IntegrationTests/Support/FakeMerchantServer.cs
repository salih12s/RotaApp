using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rota.Sim.Merchant;

namespace Rota.Notifications.IntegrationTests.Support;

/// <summary>What the fake merchant answers on one path: a status code, after a delay (real time: the sender's timeout runs on the real clock).</summary>
public sealed class MerchantBehavior
{
    public int StatusCode { get; set; } = 200;

    public TimeSpan Delay { get; set; }

    /// <summary>Where a redirect answer points; the test checks that nobody followed it.</summary>
    public string? RedirectTo { get; set; }
}

/// <summary>One request the fake merchant received.</summary>
public sealed record ReceivedWebhook(string Path, Guid EventId, string EventType, string Body, string? Signature, bool SignatureValid, DateTimeOffset At);

/// <summary>
/// A real HTTP server (Kestrel, random port) that plays a merchant's webhook endpoint. Each path ("/a", "/b") is a different merchant
/// with its own behaviour and its own secret; every request is recorded whether it was answered or not, and its signature is checked with the
/// merchant simulator's verifier, which is written independently of the sending code.
/// </summary>
public sealed class FakeMerchantServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, MerchantBehavior> _behaviors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();

    private FakeMerchantServer(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public IReadOnlyCollection<ReceivedWebhook> Received => _received;

    public string UrlFor(string path) => $"{Address.TrimEnd('/')}/{path.TrimStart('/')}";

    public MerchantBehavior Behavior(string path) => _behaviors.GetOrAdd(path.TrimStart('/'), _ => new MerchantBehavior());

    /// <summary>Tells the server which secret a path's merchant was given, so it can verify signatures like a real merchant would.</summary>
    public void SetSecret(string path, string secret) => _secrets[path.TrimStart('/')] = secret;

    public List<ReceivedWebhook> ReceivedOn(string path) => [.. _received.Where(r => r.Path == path.TrimStart('/'))];

    public static async Task<FakeMerchantServer> StartAsync(TimeProvider time)
    {
        FakeMerchantServer? server = null;
        var builder = WebApplication.CreateSlimBuilder();

        // The test folder also holds the services' appsettings.json (copied with the project references); its fixed Kestrel ports must not apply here.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.MapPost("/{path}", async (string path, HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var signature = request.Headers["Rota-Signature"].ToString();
            var secret = server!._secrets.GetValueOrDefault(path, string.Empty);
            var valid = secret.Length > 0 && WebhookVerifier.Verify(secret, signature, body, time.GetUtcNow()) == VerifyOutcome.Valid;
            var id = Guid.TryParse(request.Headers["Rota-Event-Id"].ToString(), out var parsed) ? parsed : Guid.Empty;
            server._received.Enqueue(new ReceivedWebhook(path, id, request.Headers["Rota-Event-Type"].ToString(), body, signature, valid, DateTimeOffset.UtcNow));

            var behavior = server.Behavior(path);
            if (behavior.Delay > TimeSpan.Zero)
            {
                await Task.Delay(behavior.Delay, request.HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return behavior.RedirectTo is { } target
                ? Results.Redirect(target)
                : Results.StatusCode(behavior.StatusCode);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeMerchantServer(app, address);
        return server;
    }

    public static JsonElement Json(ReceivedWebhook received) => JsonDocument.Parse(received.Body).RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
