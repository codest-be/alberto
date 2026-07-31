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

        // Read-your-writes: wait for a processor instead of sleeping for a guess.
        services.AddKeyedSingleton<ProjectionCatchUp>(moduleKey, (sp, _) =>
        {
            var options = Options(sp, moduleKey);

            // A wait that expires before the loop has had a chance to poll would report a
            // healthy processor as stuck, so the default floor is a few polls rather than a
            // flat five seconds a slow-polling module would trip over.
            var defaultTimeout = Max(TimeSpan.FromSeconds(5), options.PollingInterval * 3);

            return new ProjectionCatchUp(
                Backend(sp, moduleKey),
                sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey),
                defaultTimeout);
        });

        static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

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

            // ControlLoopAssembler composes the middleware chain once and derives
            // hasUnpairedPerEventMiddlewares internally, so both this path and the
            // shadow-rebuild path (DcbModuleBuilderExtensions) share a single assembly seam.
            var assembler = new ControlLoopAssembler(
                sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList(),
                sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey).ToList(),
                options.Retry,
                Classifier(sp, moduleKey),
                sp.GetKeyedService<IDeadLetterStore>(moduleKey),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger($"Alberto.Dcb.DeadLetter.{moduleKey}"));

            var loops = processors
                .Select(p => assembler.Create(p, head, backend, checkpoints,
                    options.PollingInterval, options.BatchSize, moduleKey,
                    executionOptionsByProcessorId.GetValueOrDefault(
                        p.ProcessorId,
                        ProcessorExecutionOptions.Default),
                    logger))
                .ToList();

            if (!options.Leases.Enabled)
                return new ControlLoopGroup(loops);

            // An empty or whitespace value is as dangerous as null: every replica misconfigured
            // this way stamps the same blank string into claimed_by, making all of them look
            // identical and defeating fencing just as completely as if the option were omitted.
            var replicaId = string.IsNullOrWhiteSpace(options.Leases.ReplicaId)
                ? Environment.MachineName
                : options.Leases.ReplicaId;

            // Backstop for a backend that permits leases but registers no manager. The built-in
            // backends are already covered earlier: the in-memory descriptor rejects leases at
            // validation time with ALB0024, and Postgres registers a manager. What reaches here
            // is a custom IAlbertoBackendDescriptor whose Validate does not object — resolve with
            // GetKeyedService so that becomes a diagnostic rather than a raw DI exception.
            var leaseManager = sp.GetKeyedService<IProcessorLeaseManager>(moduleKey)
                ?? throw new InvalidOperationException(
                    $"ALB0025: Alberto module '{moduleKey}' has Leases.Enabled = true but no " +
                    "IProcessorLeaseManager is registered under that module key, so leases " +
                    "cannot be acquired, renewed or fenced." + Environment.NewLine +
                    "  → Register an IProcessorLeaseManager for this module, switch to " +
                    ".WithPostgres(...), which provides one, or disable leases via " +
                    ".WithControlLoop(o => o with { Leases = o.Leases with { Enabled = false } }) " +
                    $"or by setting 'Alberto:Modules:{moduleKey}:ControlLoop:Leases:Enabled' to false.");

            // Enable fenced checkpoint writes and wire the group-stop handler through
            // IFencableCheckpointStore.SubscribeFenceViolation so that it coexists with
            // the per-loop cancellation handlers already subscribed by ControlLoopAssembler.
            // Any wrapper that also implements IFencableCheckpointStore is reached correctly;
            // wrapping without implementing it skips the fencing block — visible to the
            // integrator rather than a silent no-op.
            LeaseAwareControlLoopGroup? leaseGroup = null;
            if (checkpoints is IFencableCheckpointStore fencable)
            {
                // The token provider is resolved per flush rather than captured once: a replica
                // that loses a lease and takes it back is issued a new generation, and the old
                // one must stop being presented the moment the lease is gone. leaseGroup is
                // assigned just below and is captured by reference, as with OnFenceViolation.
                fencable.SetFencingContext(
                    new FencingContext(
                        moduleKey, replicaId,
                        UseProcessorLeaseFencing: true,
                        FenceTokenProvider: processorId => leaseGroup?.GetFenceToken(processorId) ?? 0));

                // Subscribed BEFORE the group is constructed so the variable is captured by
                // reference and is assigned by the time the lambda first runs.
                // Fire-and-forget: cancel the group so the fenced-out replica stops
                // dispatching duplicate side effects. The callback is synchronous;
                // discarding the Task is intentional.
                fencable.SubscribeFenceViolation(violatingProcessorId =>
                    _ = leaseGroup?.StopAsync(CancellationToken.None));
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

            // Same empty-string guard as the control loop above: a blank ReplicaId stamps the
            // same value into every replica's dead-letter claim and defeats claim isolation.
            var replicaId = string.IsNullOrWhiteSpace(options.Leases.ReplicaId)
                ? Environment.MachineName
                : options.Leases.ReplicaId;

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
