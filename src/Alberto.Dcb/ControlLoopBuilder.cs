using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb;

/// <summary>
/// Configures the per-processor control loops for an Alberto module.
/// By default each processor runs through a middleware chain that retries
/// transient failures and dead-letters events that exhaust their retry budget,
/// so a single bad event cannot halt the loop.
/// </summary>
public sealed class ControlLoopBuilder
{
    private readonly DcbModuleBuilder _moduleBuilder;
    private TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(250);
    private int _batchSize = 100;
    private TimeSpan _headRefreshInterval = TimeSpan.FromMilliseconds(100);
    private int _headWindowSize = 2000;
    private ErrorPolicy _errorPolicy = ErrorPolicy.Default;
    private readonly List<ConsumeMiddleware> _middlewares = [];
    private readonly List<BatchConsumeMiddleware> _batchMiddlewares = [];
    private TimeSpan _retryLoopPollingInterval = TimeSpan.FromMinutes(1);
    private int _retryLoopBatchSize = 10;
    private TimeSpan _retryLoopClaimLease = DeadLetterRetryLoop.DefaultClaimLeaseDuration;
    private bool _useProcessorLeases;
    private string? _replicaId;
    private bool _rebuildsEnabled;
    private bool _autoPromote = true;
    private TimeSpan _rebuildPollingInterval = TimeSpan.FromSeconds(5);
    private TimeSpan _versionRefreshInterval = TimeSpan.FromSeconds(5);

    internal ControlLoopBuilder(DcbModuleBuilder moduleBuilder) =>
        _moduleBuilder = moduleBuilder;

    public ControlLoopBuilder WithPollingInterval(TimeSpan interval)
    { _pollingInterval = interval; return this; }

    public ControlLoopBuilder WithBatchSize(int batchSize)
    { _batchSize = batchSize; return this; }

    public ControlLoopBuilder WithHeadRefreshInterval(TimeSpan interval)
    { _headRefreshInterval = interval; return this; }

    /// <summary>
    /// Configures the polling interval for the dead letter retry loop.
    /// Default: 1 minute.
    /// </summary>
    public ControlLoopBuilder WithRetryLoopPollingInterval(TimeSpan interval)
    { _retryLoopPollingInterval = interval; return this; }

    /// <summary>
    /// Configures the batch size for the dead letter retry loop.
    /// Default: 10.
    /// </summary>
    public ControlLoopBuilder WithRetryLoopBatchSize(int batchSize)
    { _retryLoopBatchSize = batchSize; return this; }

    /// <summary>
    /// Configures how long the dead letter retry loop holds a claim on an entry while dispatching.
    /// Should be longer than the slowest expected handler (e.g. long-form audio transcription) so a
    /// healthy worker won't lose its claim mid-dispatch, and short enough that a crashed worker's
    /// claims become re-dispatchable within an operationally acceptable window. Default: 15 minutes.
    /// </summary>
    public ControlLoopBuilder WithRetryLoopClaimLease(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        _retryLoopClaimLease = leaseDuration;
        return this;
    }

    /// <summary>
    /// Replaces the default error-handling policy used by the built-in
    /// retry-and-dead-letter middleware.
    /// </summary>
    public ControlLoopBuilder WithErrorPolicy(ErrorPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _errorPolicy = policy;
        return this;
    }

    /// <summary>
    /// Configures the error-handling policy by transforming the current value.
    /// <see cref="ErrorPolicy"/> is a class, not a record, so the configurator returns a new
    /// instance and reads whatever it wants to keep off the current one:
    /// <code>
    /// .WithErrorPolicy(p => new ErrorPolicy
    /// {
    ///     MaxRetries = 5,
    ///     RetryDelay = p.RetryDelay,
    ///     ErrorClassifier = p.ErrorClassifier,
    /// })
    /// </code>
    /// </summary>
    public ControlLoopBuilder WithErrorPolicy(Func<ErrorPolicy, ErrorPolicy> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _errorPolicy = configure(_errorPolicy)
            ?? throw new InvalidOperationException("ErrorPolicy configurator returned null.");
        return this;
    }

    /// <summary>
    /// Enables processor-level lease distribution. Each processor acquires a database-backed
    /// lease before starting, ensuring only one replica runs each processor at a time.
    /// On graceful shutdown, leases are released immediately for fast handoff.
    /// </summary>
    /// <param name="replicaId">
    /// Unique identifier for this replica instance.
    /// Defaults to <see cref="Environment.MachineName"/> (the container ID in Docker).
    /// </param>
    public ControlLoopBuilder WithProcessorLeases(string? replicaId = null)
    {
        _useProcessorLeases = true;
        _replicaId = replicaId;
        return this;
    }

    /// <summary>
    /// Enables zero-downtime projection rebuilds for this module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rebuild replays the whole log into a second, invisible copy of a projection's state
    /// while the live one keeps serving reads, then swaps the two in a single transaction.
    /// Nothing happens until an operator starts one — <c>alberto ops rebuild start
    /// &lt;processor&gt;</c>, or a direct call to <see cref="IProjectionRebuildStore"/> — and
    /// this method is what makes the application able to carry one out.
    /// </para>
    /// <para>
    /// Requires a backend that registers an <see cref="IProjectionRebuildStore"/>; Postgres
    /// does. Projections must resolve their rebuild version through
    /// <see cref="ProjectionStoreContext.RebuildVersion"/>, and EF projection entities must be
    /// configured with <c>ProjectionEntity</c> so their key includes the version. A projection
    /// that does neither will have its live state overwritten by the replay instead of shadowed.
    /// </para>
    /// </remarks>
    /// <param name="autoPromote">
    /// Promote a rebuild as soon as it has caught up (the default). Set false to make promotion
    /// an explicit operator step, leaving finished rebuilds parked at
    /// <see cref="RebuildStatus.Ready"/> until <c>alberto ops rebuild promote</c>.
    /// </param>
    /// <param name="pollingInterval">
    /// How often the coordinator re-reads the rebuild state machine — which is also how long an
    /// operator waits between starting a rebuild and seeing it move. Default: 5 seconds.
    /// </param>
    public ControlLoopBuilder WithRebuilds(
        bool autoPromote = true,
        TimeSpan? pollingInterval = null)
    {
        _rebuildsEnabled = true;
        _autoPromote = autoPromote;

        if (pollingInterval is { } interval)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(pollingInterval), "Interval must be positive.");
            _rebuildPollingInterval = interval;
            _versionRefreshInterval = interval;
        }

        return this;
    }

    /// <summary>
    /// Adds a custom middleware to the consume pipeline.
    /// Middlewares run in registration order (first added = outermost wrapper).
    /// The default retry-and-dead-letter middleware is always installed as the
    /// innermost layer, closest to the processor, so user middleware sees the
    /// outcome of the entire retry sequence.
    /// </summary>
    public ControlLoopBuilder WithMiddleware(ConsumeMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Adds a custom middleware to the batch consume pipeline.
    /// Batch middlewares run in registration order (first added = outermost wrapper).
    /// The built-in retry-and-dead-letter middleware is always installed as the
    /// innermost layer, closest to the processor.
    /// </summary>
    public ControlLoopBuilder WithBatchMiddleware(BatchConsumeMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _batchMiddlewares.Add(middleware);
        return this;
    }

    internal void Build()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;
        var headRefreshInterval = _headRefreshInterval;
        var headWindowSize = _headWindowSize;
        var services = _moduleBuilder.Services;
        var errorPolicy = _errorPolicy;
        var explicitMiddlewares = _middlewares.ToArray();
        var explicitBatchMiddlewares = _batchMiddlewares.ToArray();
        var retryLoopPollingInterval = _retryLoopPollingInterval;
        var retryLoopBatchSize = _retryLoopBatchSize;
        var retryLoopClaimLease = _retryLoopClaimLease;
        var useProcessorLeases = _useProcessorLeases;
        var replicaId = _replicaId ?? Environment.MachineName;

        // EventStoreHead keyed by moduleKey — resolves same backend as ConsumerBuilder used to
        services.AddKeyedSingleton<EventStoreHead>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                         ?? sp.GetKeyedService<IEventStoreBackend>(moduleKey)
                         ?? throw new InvalidOperationException(
                             $"No event store backend is registered for Alberto module '{moduleKey}'. " +
                             "Call .WithPostgres() or .WithInMemory() (or another backend) on the module " +
                             "builder before configuring a control loop.");
            var headBackend = backend as IEventStoreHeadBackend
                ?? throw new InvalidOperationException(
                    $"The event store backend registered for Alberto module '{moduleKey}' does not " +
                    "implement IEventStoreHeadBackend. All built-in backends implement this interface. " +
                    "If you are using a custom backend, implement IEventStoreHeadBackend alongside " +
                    "IEventStoreBackend to enable subscriber head tracking.");
            // Optional push-wakeup — present only when a backend registers it (e.g. Postgres LISTEN/NOTIFY).
            var signal = sp.GetKeyedService<IEventAppendedSignal>(moduleKey);
            return new EventStoreHead(headBackend, headRefreshInterval, headWindowSize,
                sp.GetService<ILogger<EventStoreHead>>(), signal);
        });
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredKeyedService<EventStoreHead>(moduleKey));

        // One ControlLoop per registered IEventProcessor
        services.AddSingleton<IHostedService>(sp =>
        {
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();
            var executionRegistrations = sp
                .GetKeyedServices<ProcessorExecutionRegistration>(moduleKey)
                .ToList();

            // Fail fast if two processors share the same ID — they'd share checkpoint
            // state, causing one to silently skip events the other already advanced past.
            var duplicates = processors
                .GroupBy(p => p.ProcessorId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Module '{moduleKey}' has duplicate processor IDs: [{string.Join(", ", duplicates)}]. " +
                    "Each reactor and projection must have a unique processorId because they share checkpoint storage.");
            }

            var duplicateExecutionRegistrations = executionRegistrations
                .GroupBy(r => r.ProcessorId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateExecutionRegistrations.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Module '{moduleKey}' has duplicate processor execution registrations: " +
                    $"[{string.Join(", ", duplicateExecutionRegistrations)}].");
            }

            var executionOptionsByProcessorId = executionRegistrations
                .ToDictionary(r => r.ProcessorId, r => r.Options, StringComparer.Ordinal);

            var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
            // DX-7: surface a clear Alberto-specific message instead of a raw keyed-DI exception.
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                         ?? sp.GetKeyedService<IEventStoreBackend>(moduleKey)
                         ?? throw new InvalidOperationException(
                             $"No event store backend is registered for Alberto module '{moduleKey}'. " +
                             "Call .WithPostgres() or .WithInMemory() (or another backend) on the module " +
                             "builder before configuring a control loop.");
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var logger = sp.GetService<ILogger<ControlLoop>>();

            // Compose the middleware chain (outermost first):
            //   [DI-registered middlewares...]   ← e.g. WithTelemetry()
            //   [explicit middlewares...]        ← .WithMiddleware(...)
            //   RetryAndDeadLetter               ← always innermost
            //   processor.ProcessEventAsync       ← terminal
            var (middlewares, batchMiddlewares, hasUnpairedPerEventMiddlewares) =
                ComposeMiddleware(sp, moduleKey, errorPolicy,
                    explicitMiddlewares, explicitBatchMiddlewares);

            var loops = processors
                .Select(p => new ControlLoop(p, head, backend, checkpoints,
                    pollingInterval, batchSize, moduleKey, middlewares, batchMiddlewares,
                    hasUnpairedPerEventMiddlewares,
                    executionOptionsByProcessorId.GetValueOrDefault(
                        p.ProcessorId,
                        ProcessorExecutionOptions.Default),
                    logger))
                .ToList();

            if (useProcessorLeases)
            {
                var leaseManager = sp.GetRequiredKeyedService<IProcessorLeaseManager>(moduleKey);

                // Enable fenced checkpoint writes and wire the fence-violation callback
                // through IFencableCheckpointStore so we don't downcast to the concrete
                // type (P3.4 / COR-5). Any wrapper around CachingCheckpointStore that
                // also implements IFencableCheckpointStore will be reached correctly;
                // wrapping without implementing IFencableCheckpointStore will still
                // compile — the fencing block is simply skipped — which is visible to
                // the integrator rather than silently dropping the callback.
                LeaseAwareControlLoopGroup? leaseGroup = null;
                if (checkpoints is IFencableCheckpointStore fencable)
                {
                    fencable.SetFencingContext(new FencingContext(moduleKey, replicaId, UseProcessorLeaseFencing: true));
                    // Wire the fence-violation callback BEFORE the group is returned so
                    // the variable is captured by reference and will be set when the
                    // lambda is first invoked (from a periodic timer, always after init).
                    fencable.OnFenceViolation = violatingProcessorId =>
                    {
                        // Fire-and-forget: cancel the control loop group immediately so
                        // the fenced-out replica stops dispatching duplicate side effects.
                        // The callback is Action<string> (synchronous); discarding the
                        // Task is intentional — CancelAsync() returns quickly and the
                        // async tail only waits on already-draining workers.
                        _ = leaseGroup?.StopAsync(CancellationToken.None);
                    };
                }

                leaseGroup = new LeaseAwareControlLoopGroup(
                    loops, leaseManager, moduleKey, replicaId,
                    sp.GetService<ILogger<LeaseAwareControlLoopGroup>>());
                return leaseGroup;
            }

            return new ControlLoopGroup(loops);
        });

        // Dead letter retry loop — dedicated polling for CLI-requested retries
        services.AddSingleton<IHostedService>(sp =>
        {
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey)
                ?? throw new InvalidOperationException(
                    $"No IDeadLetterStore registered for module '{moduleKey}'. " +
                    "Dead letter retry loop requires a dead letter store.");
            var logger = sp.GetService<ILogger<DeadLetterRetryLoop>>();

            // Compose middleware chain (same as ControlLoop)
            var (middlewares, _, _) = ComposeMiddleware(
                sp, moduleKey, errorPolicy, explicitMiddlewares, explicitBatchMiddlewares);

            var retryLoops = processors
                .Select(p => new DeadLetterRetryLoop(
                    p,
                    deadLetterStore,
                    retryLoopPollingInterval,
                    retryLoopBatchSize,
                    middlewares,
                    logger,
                    retryLoopClaimLease,
                    replicaId))
                .ToList();
            return new DeadLetterRetryLoopGroup(retryLoops);
        });

        if (_rebuildsEnabled)
            BuildRebuildPipeline();
    }

    /// <summary>
    /// Registers the pieces that let this module carry out a zero-downtime projection rebuild:
    /// the version source every state store resolves through, a factory for shadow control
    /// loops, and the coordinator that drives them.
    /// </summary>
    private void BuildRebuildPipeline()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var services = _moduleBuilder.Services;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;
        var errorPolicy = _errorPolicy;
        var explicitMiddlewares = _middlewares.ToArray();
        var explicitBatchMiddlewares = _batchMiddlewares.ToArray();
        var versionRefreshInterval = _versionRefreshInterval;
        var coordinatorOptions = new RebuildCoordinatorOptions(_rebuildPollingInterval, _autoPromote);

        services.AddKeyedSingleton(moduleKey, (sp, _) =>
        {
            var store = sp.GetKeyedService<IProjectionRebuildStore>(moduleKey)
                ?? throw new InvalidOperationException(
                    $"Alberto module '{moduleKey}' enables projection rebuilds, but its event store " +
                    "backend does not register an IProjectionRebuildStore. Rebuilds need a backend " +
                    "that can hold the rebuild state machine — call .WithPostgres() on the module.");
            return new ProjectionVersions(store, versionRefreshInterval);
        });

        services.AddKeyedSingleton(moduleKey, (sp, _) => new ShadowControlLoopFactory(processor =>
        {
            var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                          ?? sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var (middlewares, batchMiddlewares, hasUnpaired) = ComposeMiddleware(
                sp, moduleKey, errorPolicy, explicitMiddlewares, explicitBatchMiddlewares);

            // The shadow loop runs on the module's default execution settings rather than the
            // live processor's registration: a rebuild replays the whole log and always wants
            // batching, which ShadowProcessor supplies even for a projection that does not
            // implement IBatchableProcessor itself.
            return new ControlLoop(
                processor, head, backend, checkpoints, pollingInterval, batchSize, moduleKey,
                middlewares, batchMiddlewares, hasUnpaired,
                ProcessorExecutionOptions.Default,
                sp.GetService<ILogger<ControlLoop>>());
        }));

        services.AddSingleton<IHostedService>(sp => new RebuildCoordinator(
            sp.GetKeyedServices<RebuildableProjection>(moduleKey).ToList(),
            sp.GetRequiredKeyedService<IProjectionRebuildStore>(moduleKey),
            sp.GetRequiredKeyedService<ProjectionVersions>(moduleKey),
            sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey),
            sp.GetRequiredKeyedService<ShadowControlLoopFactory>(moduleKey),
            sp.GetKeyedServices<IProjectionStateClearer>(moduleKey).ToList(),
            coordinatorOptions,
            sp.GetService<ILogger<RebuildCoordinator>>()));
    }

    /// <summary>
    /// Builds the consume chains for a loop, outermost first: DI-registered middleware, then
    /// explicitly configured middleware, then retry-and-dead-letter closest to the processor.
    /// </summary>
    /// <returns>
    /// The per-event chain, the batch chain, and whether some per-event middleware has no batch
    /// equivalent — which a processor that requires batching must be told about rather than
    /// silently losing.
    /// </returns>
    private static (List<ConsumeMiddleware> PerEvent, List<BatchConsumeMiddleware> Batch, bool HasUnpaired)
        ComposeMiddleware(
            IServiceProvider sp,
            string moduleKey,
            ErrorPolicy errorPolicy,
            ConsumeMiddleware[] explicitMiddlewares,
            BatchConsumeMiddleware[] explicitBatchMiddlewares)
    {
        var diMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList();
        var diBatchMiddlewares = sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey).ToList();
        var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);

        var middlewares = new List<ConsumeMiddleware>(diMiddlewares);
        middlewares.AddRange(explicitMiddlewares);
        middlewares.Add(ConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

        var batchMiddlewares = new List<BatchConsumeMiddleware>(diBatchMiddlewares);
        batchMiddlewares.AddRange(explicitBatchMiddlewares);
        batchMiddlewares.Add(BatchConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

        var hasUnpaired =
            diMiddlewares.Count > diBatchMiddlewares.Count ||
            explicitMiddlewares.Length > explicitBatchMiddlewares.Length;

        return (middlewares, batchMiddlewares, hasUnpaired);
    }
}
