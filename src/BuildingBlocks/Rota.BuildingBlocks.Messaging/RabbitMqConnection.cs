using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace Rota.BuildingBlocks.Messaging;

/// <summary>
/// One TCP connection per process, opened on first use and recovered automatically after a broker
/// restart (the client re-opens channels and consumers). Each component takes its own channel:
/// channels are cheap, connections are not.
/// </summary>
public sealed class RabbitMqConnection(IConfiguration configuration) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);

        // Tracking = BasicPublishAsync completes only after the broker confirmed the message,
        // and throws (PublishException) on a nack. That is the publisher confirm the relay relies on.
        return await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: publisherConfirms, publisherConfirmationTrackingEnabled: publisherConfirms),
            cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        // Once opened, the connection is never replaced. While the broker is down it reports "not open"
        // but is recovering by itself (and the relay and every consumer share it): swapping it for a
        // new one would kill the channels of everyone else. Operations on it just fail until recovery
        // finishes, and their callers retry.
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var uri = configuration.GetConnectionString(MessagingOptions.ConnectionName)
                ?? throw new InvalidOperationException($"Connection string '{MessagingOptions.ConnectionName}' is not configured.");

            var factory = new ConnectionFactory
            {
                Uri = new Uri(uri),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
                ClientProvidedName = AppDomain.CurrentDomain.FriendlyName,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
