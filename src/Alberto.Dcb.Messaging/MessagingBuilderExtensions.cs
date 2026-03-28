using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Extension methods for wiring the transactional outbox into the Alberto consumer pipeline.
/// </summary>
public static class MessagingBuilderExtensions
{
    /// <summary>
    /// Registers an <see cref="OutboxHandler"/> as an event processor on the module.
    /// The handler maps events to outbox entries using <paramref name="configureMappings"/>.
    /// </summary>
    /// <param name="builder">The module builder to configure.</param>
    /// <param name="configureMappings">A delegate that populates the message mapping registry.</param>
    /// <param name="outboxStore">The outbox store used to persist entries.</param>
    /// <param name="transport">
    /// Optional transport used by an <see cref="OutboxRelay"/> hosted service.
    /// When provided, an <see cref="OutboxRelay"/> is registered as a hosted service automatically.
    /// </param>
    public static DcbModuleBuilder WithOutbox(
        this DcbModuleBuilder builder,
        Action<IMessageMappingRegistry> configureMappings,
        IOutboxStore outboxStore,
        IMessageTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(configureMappings);
        ArgumentNullException.ThrowIfNull(outboxStore);

        var registry = new MessageMappingRegistry();
        configureMappings(registry);

        var handler = new OutboxHandler(registry, outboxStore);

        // Register the handler as a keyed IEventProcessor so the ControlLoop picks it up
        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, handler);

        // Optionally wire up the relay as a hosted service
        if (transport is not null)
        {
            builder.Services.AddSingleton<IHostedService>(
                _ => new OutboxRelay(outboxStore, transport));
        }

        return builder;
    }
}
