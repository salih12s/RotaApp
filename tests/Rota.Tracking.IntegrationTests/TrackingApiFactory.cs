using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Rota.TestKit;

namespace Rota.Tracking.IntegrationTests;

/// <summary>The real Tracking API in memory (no port), wired to the test containers. Environment stays Development (DevAuth).</summary>
public sealed class TrackingApiFactory(string redisConnectionString, string postgresConnectionString) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The service's clock. Fixed at the test start: a test that needs time to pass advances it, so
    /// freshness rules ("not older than 30 s") are tested without waiting. Tests that advance it use
    /// their own factory, not the shared one.
    /// </summary>
    public FakeTimeProvider Time { get; } = new(TestClock.Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        builder.UseSetting("ConnectionStrings:Tracking", postgresConnectionString);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    /// <summary>A gRPC channel into the in-memory server. The caller disposes it.</summary>
    public GrpcChannel CreateGrpcChannel()
    {
        var client = CreateDefaultClient(new Http2ResponseHandler());
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });
    }

    /// <summary>Call metadata that says who is calling (DevAuth, ADR-006); gRPC metadata keys are lower-case.</summary>
    public static Metadata As(string devUser) => new() { { DevUsers.HeaderName.ToLowerInvariant(), devUser } };

    /// <summary>
    /// The in-memory test server answers with HTTP/1.1 framing; gRPC clients insist on HTTP/2, so the
    /// response version is relabelled (the approach from the official gRPC testing guidance).
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
