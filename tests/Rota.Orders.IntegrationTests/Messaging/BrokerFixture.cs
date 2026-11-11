using System.Net;
using System.Net.Sockets;
using Rota.Orders.IntegrationTests.Messaging;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(BrokerFixture))]

namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>A real RabbitMQ in Docker. Same image as deploy/docker-compose.yml.</summary>
public sealed class Broker : IAsyncDisposable
{
    public const string Image = "rabbitmq:4.4.0-management-alpine";

    private readonly RabbitMqContainer _container;

    private Broker(RabbitMqContainer container) => _container = container;

    public static async Task<Broker> StartAsync(bool fixedPort, CancellationToken cancellationToken)
    {
        var builder = new RabbitMqBuilder(Image);
        if (fixedPort)
        {
            // A stopped container otherwise comes back on another host port and every client would
            // point at a dead address. The chaos test needs the broker to return where it was.
            builder = builder.WithPortBinding(FreePort(), 5672);
        }

        var container = builder.Build();
        await container.StartAsync(cancellationToken);
        return new Broker(container);
    }

    /// <summary>Stops the broker process; durable queues and messages stay on its disk.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => _container.StopAsync(cancellationToken);

    public Task StartAgainAsync(CancellationToken cancellationToken) => _container.StartAsync(cancellationToken);

    /// <summary>
    /// A fresh virtual host (an isolated namespace of exchanges and queues) so every test has the
    /// broker to itself: two tests using the same queue names cannot steal each other's messages.
    /// </summary>
    public async Task<string> CreateVirtualHostAsync(CancellationToken cancellationToken)
    {
        var name = $"t{Guid.NewGuid():N}";
        // The container creates its own default user; ask it instead of assuming a name.
        var credentials = new Uri(_container.GetConnectionString()).UserInfo;
        var user = credentials.Split(':')[0];

        await RunAsync(["rabbitmqctl", "add_vhost", name], cancellationToken);
        await RunAsync(["rabbitmqctl", "set_permissions", "-p", name, user, ".*", ".*", ".*"], cancellationToken);
        return $"amqp://{credentials}@{_container.Hostname}:{_container.GetMappedPublicPort(5672)}/{name}";
    }

    private async Task RunAsync(string[] command, CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{string.Join(' ', command)}' failed (exit {result.ExitCode}): {result.Stderr} {result.Stdout}");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>One shared broker for the messaging tests, started only when a messaging test runs.</summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    private readonly Lazy<Task<Broker>> _broker = new(() => Broker.StartAsync(fixedPort: false, CancellationToken.None));

    public Task<Broker> GetAsync() => _broker.Value;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_broker.IsValueCreated)
        {
            await (await _broker.Value).DisposeAsync();
        }
    }
}
