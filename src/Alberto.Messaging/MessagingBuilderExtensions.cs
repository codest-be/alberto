using Alberto.Configuration;
using Alberto.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Messaging;

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
    /// Reusing the same instance in more than one registration shares one start/stop lifecycle
    /// within the resulting service provider. Alberto does not dispose the caller-owned instance.
    /// </param>
    /// <param name="relayBatchSize">
    /// Maximum number of outbox entries the relay processes per poll cycle. Defaults to 1.
    /// </param>
    /// <param name="relayClaimLease">
    /// How long the relay owns a claimed entry before another relay may recover it.
    /// Defaults to <see cref="OutboxRelay.DefaultClaimLease"/>.
    /// </param>
    /// <param name="deliveredRetention">
    /// How long a delivered entry is kept before an <see cref="OutboxRetentionService"/> removes
    /// it. Defaults to <see cref="OutboxRetentionService.DefaultRetention"/>. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> to keep delivered entries forever — the outbox then
    /// grows without bound, so pair it with a purge you run yourself.
    /// </param>
    /// <param name="retentionSweepInterval">
    /// How often the retention sweep runs. Defaults to
    /// <see cref="OutboxRetentionService.DefaultSweepInterval"/>.
    /// </param>
    /// <remarks>
    /// The retention sweep is registered whether or not a <paramref name="transport"/> is —
    /// entries reach <c>delivered</c> by being delivered, and a host that relays elsewhere still
    /// accumulates them.
    /// </remarks>
    public static DcbModuleBuilder WithOutbox(
        this DcbModuleBuilder builder,
        Action<IMessageMappingRegistry> configureMappings,
        IOutboxStore outboxStore,
        IMessageTransport? transport = null,
        int relayBatchSize = 1,
        TimeSpan? relayClaimLease = null,
        TimeSpan? deliveredRetention = null,
        TimeSpan? retentionSweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(configureMappings);
        ArgumentNullException.ThrowIfNull(outboxStore);

        var registry = new MessageMappingRegistry();
        configureMappings(registry);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = OutboxHandler.ProcessorIdValue,
            Kind = ProcessorKind.Reactor,
            Execution = ProcessorExecutionOptions.Default,
            HandlerType = typeof(OutboxHandler),
        });

        builder.Register(context =>
        {
            // Stamp the module key onto the registry so the convenience Map extension methods can
            // resolve the module-keyed EventSerializer at runtime (for upcaster support).
            registry.ModuleKey = context.ModuleKey;

            // Register the handler as a keyed IEventProcessor so the ControlLoop picks it up.
            // OutboxHandler implements IBatchableProcessor, satisfying the default Required batching mode.
            context.Services.AddKeyedSingleton<IEventProcessor>(context.ModuleKey, (sp, _) =>
                new OutboxHandler(registry, outboxStore, sp.GetRequiredService<IServiceScopeFactory>()));

            context.Services.AddSingleton<IHostedService>(
                sp => new OutboxRetentionService(
                    outboxStore,
                    deliveredRetention,
                    retentionSweepInterval,
                    sp.GetService<TimeProvider>(),
                    sp.GetService<ILogger<OutboxRetentionService>>()));

            // Optionally wire up the relay as a hosted service
            if (transport is not null)
            {
                context.Services.TryAddSingleton<OutboxTransportLifecycleRegistry>();
                context.Services.AddSingleton<IHostedService>(
                    sp => new OutboxRelay(
                        outboxStore,
                        sp.GetRequiredService<OutboxTransportLifecycleRegistry>()
                            .CreateLease(transport),
                        batchSize: relayBatchSize,
                        claimLease: relayClaimLease));
            }
        });

        return builder;
    }
}
