using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Rota.Contracts.Events;

namespace Rota.BuildingBlocks.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Adds the outbox relay for a service's DbContext. Does nothing until <c>Messaging:Enabled</c> is
    /// true at runtime, so a service can still start without a broker in tests.
    /// </summary>
    public static IHostApplicationBuilder AddRotaMessaging<TContext>(this IHostApplicationBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.SectionName)
            .Validate(o => o.EffectiveRetryDelays.All(d => d > TimeSpan.Zero), "Messaging:RetryDelays must be positive.")
            .Validate(o => o.RelayBatchSize > 0 && o.ConsumerPrefetch > 0, "Messaging:RelayBatchSize and ConsumerPrefetch must be positive.")
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<RabbitMqConnection>();
        builder.Services.AddHostedService<OutboxRelay<TContext>>();
        return builder;
    }

    /// <summary>Subscribes <typeparamref name="THandler"/> to <typeparamref name="TEvent"/> on its own queue.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="queue">e.g. <c>q.dispatch.order-created</c>: one queue per consumer.</param>
    /// <param name="consumerName">e.g. <c>Dispatch.OrderCreatedConsumer</c>: the inbox key. Never rename it after go-live, or old messages look new.</param>
    public static IHostApplicationBuilder AddRotaConsumer<TContext, TEvent, THandler>(
        this IHostApplicationBuilder builder, string queue, string consumerName)
        where TContext : DbContext
        where TEvent : IIntegrationEvent
        where THandler : class, IMessageHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        var definition = new ConsumerDefinition(queue, consumerName);
        builder.Services.AddScoped<THandler>();
        builder.Services.AddSingleton<IHostedService>(sp =>
            ActivatorUtilities.CreateInstance<ConsumerService<TContext, TEvent, THandler>>(sp, definition));
        return builder;
    }
}
