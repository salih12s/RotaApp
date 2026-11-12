using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Rota.TestKit;

namespace Rota.Dispatch.IntegrationTests;

/// <summary>
/// The real Dispatch API in memory (no port), wired to the test database, Redis and the fake Tracking. Environment stays
/// Development (DevAuth, ADR-006). The background worker is off by default: tests decide when a search or timeout round runs.
/// </summary>
public sealed class DispatchApiFactory(
    string dispatchConnectionString,
    string redisConnectionString,
    string trackingAddress,
    IReadOnlyDictionary<string, string>? extraSettings = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The service's clock: frozen at the test start, moved by the test (an offer's 30 seconds pass in one call).</summary>
    public FakeTimeProvider Time { get; } = new(TestClock.Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dispatch", dispatchConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        builder.UseSetting("Dispatch:TrackingAddress", trackingAddress);
        builder.UseSetting("Dispatch:WorkersEnabled", "false");
        builder.UseSetting("Messaging:Enabled", "false");

        // The production deadline is 200 ms. The first gRPC call of a test process also pays for JIT and connection setup,
        // so most tests run with a roomy deadline; the test about the deadline itself puts the real 200 ms back.
        builder.UseSetting("Dispatch:NearbyDeadline", "00:00:05");

        // Applied after the defaults, so a test can switch a default back (e.g. WorkersEnabled).
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public HttpClient CreateClientAs(string devUser) => CreateClient().As(devUser);

    /// <summary>A gRPC channel into the in-memory server. The caller disposes it.</summary>
    public GrpcChannel CreateGrpcChannel()
    {
        var client = CreateDefaultClient(new Http2ResponseHandler());
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });
    }

    /// <summary>Call metadata that says who is calling (DevAuth); gRPC metadata keys are lower-case.</summary>
    public static Metadata As(string devUser) => new() { { DevUsers.HeaderName.ToLowerInvariant(), devUser } };

    /// <summary>
    /// The in-memory test server answers with HTTP/1.1 framing; gRPC clients insist on HTTP/2, so the response version is
    /// relabelled (the approach from the official gRPC testing guidance).
    /// </summary>
    private sealed class Http2ResponseHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Version = HttpVersion.Version20;
            return response;
        }
    }
}
