using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Rota.Contracts.Tracking.V1;

namespace Rota.Dispatch.IntegrationTests.Support;

/// <summary>What the fake Tracking answers, and what it saw. Tests change it while the server runs.</summary>
public sealed class FakeTrackingState
{
    private int _calls;

    /// <summary>The couriers every <c>GetNearbyCouriers</c> returns (already "near" and "fresh": Tracking's job, not under test here).</summary>
    public IReadOnlyList<NearbyCourier> Couriers { get; set; } = [];

    /// <summary>Makes every answer slow (a real delay: gRPC deadlines run on the real clock).</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Given the 1-based call number, the status to fail that call with, or null to answer normally.</summary>
    public Func<int, StatusCode?>? FailWith { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public string? LastCaller { get; internal set; }

    internal int NextCall() => Interlocked.Increment(ref _calls);
}

/// <summary>
/// A real gRPC server (Kestrel, HTTP/2, random port) that plays Tracking. Dispatch talks to it over a real socket, so the
/// deadline and the retry policy of its client are really exercised; Tracking's own behaviour is tested in its own project.
/// </summary>
public sealed class FakeTrackingServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeTrackingServer(WebApplication app, string address, FakeTrackingState state)
    {
        _app = app;
        Address = address;
        State = state;
    }

    public string Address { get; }

    public FakeTrackingState State { get; }

    public static async Task<FakeTrackingServer> StartAsync()
    {
        var state = new FakeTrackingState();

        var builder = WebApplication.CreateSlimBuilder();

        // The test folder also holds Dispatch's appsettings.json (it is copied along with the project reference). Its fixed Kestrel
        // ports (5102/5202) would make every fake server fight for the same port; this server only listens where it says below.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(state);
        builder.Services.AddGrpc();

        var app = builder.Build();
        app.MapGrpcService<FakeTrackingService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeTrackingServer(app, address, state);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class FakeTrackingService(FakeTrackingState state) : CourierLocationQuery.CourierLocationQueryBase
    {
        public override async Task<GetNearbyCouriersResponse> GetNearbyCouriers(GetNearbyCouriersRequest request, ServerCallContext context)
        {
            var call = state.NextCall();
            state.LastCaller = context.RequestHeaders.GetValue("x-dev-user");

            if (state.Delay > TimeSpan.Zero)
            {
                await Task.Delay(state.Delay, context.CancellationToken);
            }

            if (state.FailWith?.Invoke(call) is { } code)
            {
                throw new RpcException(new Status(code, "injected failure"));
            }

            var response = new GetNearbyCouriersResponse();
            response.Couriers.AddRange(state.Couriers);
            return response;
        }
    }
}
