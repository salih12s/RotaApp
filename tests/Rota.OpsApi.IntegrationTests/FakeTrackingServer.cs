using System.Net;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Rota.Contracts.Tracking.V1;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>What the fake Tracking saw: how many <c>GetLastLocations</c> calls came in, how many couriers each asked for, and who called.</summary>
public sealed class FakeTrackingState
{
    private readonly List<int> _batches = [];

    public IReadOnlyList<int> Batches
    {
        get
        {
            lock (_batches)
            {
                return [.. _batches];
            }
        }
    }

    public string? LastCaller { get; private set; }

    /// <summary>How long Tracking "thinks" before answering: lets a test make it slower than the Ops API's deadline.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Couriers Tracking "has never heard of": they are left out of the answer.</summary>
    public HashSet<Guid> Unknown { get; } = [];

    internal void Record(int count, string? caller)
    {
        lock (_batches)
        {
            _batches.Add(count);
            LastCaller = caller;
        }
    }
}

/// <summary>A real gRPC server (Kestrel, HTTP/2, random port) that plays Tracking, so the Ops API talks to it over a real socket.</summary>
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

        // The test folder also holds the services' appsettings.json files (copied with the project references); they must not decide the ports.
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
}

internal sealed class FakeTrackingService(FakeTrackingState state) : CourierLocationQuery.CourierLocationQueryBase
{
    public override async Task<GetLastLocationsResponse> GetLastLocations(GetLastLocationsRequest request, ServerCallContext context)
    {
        state.Record(request.CourierIds.Count, context.RequestHeaders.GetValue("x-dev-user") ?? context.RequestHeaders.GetValue("authorization"));
        if (state.Delay > TimeSpan.Zero)
        {
            await Task.Delay(state.Delay, context.CancellationToken);
        }

        var response = new GetLastLocationsResponse();
        foreach (var id in request.CourierIds.Select(Guid.Parse).Where(id => !state.Unknown.Contains(id)))
        {
            response.Locations.Add(new CourierLocation { CourierId = id.ToString("D"), Lat = 41.0, Lon = 29.0, RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch) });
        }

        return response;
    }
}
