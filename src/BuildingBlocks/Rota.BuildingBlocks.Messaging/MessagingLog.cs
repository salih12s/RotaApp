using Microsoft.Extensions.Logging;

namespace Rota.BuildingBlocks.Messaging;

internal static partial class MessagingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Messaging is disabled ({Component}); nothing will be published or consumed")]
    public static partial void Disabled(ILogger logger, string component);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay failed; retrying")]
    public static partial void RelayFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox message {MessageId} ({RoutingKey}) failed (attempt {Attempt}); it stays in the outbox")]
    public static partial void PublishFailed(ILogger logger, Exception exception, Guid messageId, string routingKey, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup removed {Count} processed rows older than {Cutoff:O}")]
    public static partial void Cleaned(ILogger logger, int count, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} lost its broker channel; reconnecting")]
    public static partial void ConsumerReconnecting(ILogger logger, Exception exception, string consumer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer {Consumer} dropped a duplicate message {MessageId}")]
    public static partial void Duplicate(ILogger logger, string consumer, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on {MessageId} (attempt {Attempt}); moving to {Target}")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string consumer, string messageId, int attempt, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} could not move message {MessageId} to {Target}; leaving it for redelivery")]
    public static partial void MoveFailed(ILogger logger, Exception exception, string consumer, string messageId, string target);
}
