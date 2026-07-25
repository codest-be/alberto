using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb;

/// <summary>
/// Registers the per-processor control loops, the event store head, and the dead letter retry
/// loop for one module. Every setting is read from the validated
/// <see cref="AlbertoModuleDefinition"/> at resolution time, so values supplied through
/// configuration reach the running loop.
/// </summary>
internal static class ControlLoopRegistration
{
    internal static void Register(AlbertoModuleContext context)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        static ControlLoopOptions Options(IServiceProvider sp, string moduleKey) =>
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey).ControlLoop;

        static IErrorClassifier Classifier(IServiceProvider sp, string moduleKey) =>
            sp.GetKeyedService<IErrorClassifier>(moduleKey) ?? DefaultErrorClassifier.Instance;

        static IEventStoreBackend Backend(IServiceProvider sp, string moduleKey) =>
            sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
            ?? sp.GetKeyedService<IEventStoreBackend>(moduleKey)
            ?? throw new InvalidOperationException(
                $"No event store backend is registered for Alberto module '{moduleKey}'. " +
                "Call .WithPostgres(...) or .WithInMemory() on the module builder.");

        services.AddKeyedSingleton<EventStoreHead>(moduleKey, (sp, _) =>
        {
            var options = Options(sp, moduleKey);
            var headBackend = Backend(sp, moduleKey) as IEventStoreHeadBackend
                ?? throw new InvalidOperationException(
                    $"The event store backend registered for Alberto module '{moduleKey}' does not " +
                    "implement IEventStoreHeadBackend. All built-in backends implement this interface. " +
                    "If you are using a custom backend, implement IEventStoreHeadBackend alongside " +
                    "IEventStoreBackend to enable subscriber head tracking.");

            // Optional push-wakeup — present only when a backend registers it (e.g. Postgres LISTEN/NOTIFY).
            var signal = sp.GetKeyedService<IEventAppendedSignal>(moduleKey);

            return new EventStoreHead(
                headBackend,
                options.HeadRefreshInterval,
                options.HeadWindowSize,
                sp.GetService<ILogger<EventStoreHead>>(),
                signal);
        });

        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredKeyedService<EventStoreHead>(moduleKey));

        // One ControlLoop per registered IEventProcessor.
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = Options(sp, moduleKey);
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();

            var executionOptionsByProcessorId = sp
                .GetKeyedServices<ProcessorExecutionRegistration>(moduleKey)
                .ToDictionary(r => r.ProcessorId, r => r.Options, StringComparer.Ordinal);

            var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
            var backend = Backend(sp, moduleKey);
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var logger = sp.GetService<ILogger<ControlLoop>>();

            // Compose the middleware chain (outermost first):
            //   [keyed ConsumeMiddleware...]   ← WithTelemetry(), AddConsumeMiddleware(...)
            //   RetryAndDeadLetter             ← always innermost
            //   processor.ProcessEventAsync    ← terminal
            var diMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList();
            var diBatchMiddlewares = sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey).ToList();
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);
            var classifier = Classifier(sp, moduleKey);

            var middlewares = new List<ConsumeMiddleware>(diMiddlewares)
            {
                ConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            var batchMiddlewares = new List<BatchConsumeMiddleware>(diBatchMiddlewares)
            {
                BatchConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            // A per-event middleware with no batch counterpart cannot be honoured on the batch
            // path, so batching falls back to per-event dispatch rather than silently skipping it.
            var hasUnpairedPerEventMiddlewares = diMiddlewares.Count > diBatchMiddlewares.Count;

            var loops = processors
                .Select(p => new ControlLoop(p, head, backend, checkpoints,
                    options.PollingInterval, options.BatchSize, moduleKey, middlewares, batchMiddlewares,
                    hasUnpairedPerEventMiddlewares,
                    executionOptionsByProcessorId.GetValueOrDefault(
                        p.ProcessorId,
                        ProcessorExecutionOptions.Default),
                    logger))
                .ToList();

            if (!options.Leases.Enabled)
                return new ControlLoopGroup(loops);

            var replicaId = options.Leases.ReplicaId ?? Environment.MachineName;
            var leaseManager = sp.GetRequiredKeyedService<IProcessorLeaseManager>(moduleKey);

            // Enable fenced checkpoint writes and wire the fence-violation callback through
            // IFencableCheckpointStore so we don't downcast to the concrete type. Any wrapper
            // around CachingCheckpointStore that also implements IFencableCheckpointStore is
            // reached correctly; wrapping without implementing it still compiles — the fencing
            // block is simply skipped — which is visible to the integrator rather than silently
            // dropping the callback.
            LeaseAwareControlLoopGroup? leaseGroup = null;
            if (checkpoints is IFencableCheckpointStore fencable)
            {
                fencable.SetFencingContext(
                    new FencingContext(moduleKey, replicaId, UseProcessorLeaseFencing: true));

                // Wired BEFORE the group is constructed so the variable is captured by reference
                // and is set by the time the lambda first runs (from a periodic timer).
                fencable.OnFenceViolation = violatingProcessorId =>
                {
                    // Fire-and-forget: cancel the group so the fenced-out replica stops
                    // dispatching duplicate side effects. The callback is synchronous;
                    // discarding the Task is intentional.
                    _ = leaseGroup?.StopAsync(CancellationToken.None);
                };
            }

            leaseGroup = new LeaseAwareControlLoopGroup(
                loops, leaseManager, moduleKey, replicaId,
                sp.GetService<ILogger<LeaseAwareControlLoopGroup>>());

            return leaseGroup;
        });

        // Dead letter retry loop — dedicated polling for CLI-requested retries.
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = Options(sp, moduleKey);
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey)
                ?? throw new InvalidOperationException(
                    $"No IDeadLetterStore registered for module '{moduleKey}'. " +
                    "The dead letter retry loop requires a dead letter store.");

            var classifier = Classifier(sp, moduleKey);
            var logger = sp.GetService<ILogger<DeadLetterRetryLoop>>();

            var middlewares = new List<ConsumeMiddleware>(sp.GetKeyedServices<ConsumeMiddleware>(moduleKey))
            {
                ConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            var replicaId = options.Leases.ReplicaId ?? Environment.MachineName;

            var retryLoops = processors
                .Select(p => new DeadLetterRetryLoop(
                    p,
                    deadLetterStore,
                    options.DeadLetterRetry.PollingInterval,
                    options.DeadLetterRetry.BatchSize,
                    middlewares,
                    logger,
                    options.DeadLetterRetry.ClaimLease,
                    replicaId))
                .ToList();

            return new DeadLetterRetryLoopGroup(retryLoops);
        });
    }
}
