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
    private bool _useProcessorLeases;
    private string? _replicaId;

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
    /// Useful with <c>with</c>-style record updates:
    /// <code>.WithErrorPolicy(p => p with { MaxRetries = 5 })</code>
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
        var useProcessorLeases = _useProcessorLeases;
        var replicaId = _replicaId ?? Environment.MachineName;

        // EventStoreHead keyed by moduleKey — resolves same backend as ConsumerBuilder used to
        services.AddKeyedSingleton<EventStoreHead>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                         ?? sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            return new EventStoreHead(backend, headRefreshInterval, headWindowSize,
                sp.GetService<ILogger<EventStoreHead>>());
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
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                         ?? sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var logger = sp.GetService<ILogger<ControlLoop>>();

            // Compose the middleware chain (outermost first):
            //   [DI-registered middlewares...]   ← e.g. WithTelemetry()
            //   [explicit middlewares...]        ← .WithMiddleware(...)
            //   RetryAndDeadLetter               ← always innermost
            //   processor.ProcessEventAsync       ← terminal
            var diMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey);
            var diBatchMiddlewares = sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey);
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);

            var middlewares = new List<ConsumeMiddleware>();
            middlewares.AddRange(diMiddlewares);
            middlewares.AddRange(explicitMiddlewares);
            middlewares.Add(ConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

            var batchMiddlewares = new List<BatchConsumeMiddleware>();
            batchMiddlewares.AddRange(diBatchMiddlewares);
            batchMiddlewares.AddRange(explicitBatchMiddlewares);
            batchMiddlewares.Add(BatchConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

            var hasUnpairedPerEventMiddlewares =
                diMiddlewares.Count() > diBatchMiddlewares.Count() ||
                explicitMiddlewares.Length > explicitBatchMiddlewares.Length;

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

                // Enable fenced checkpoint writes to prevent zombie processors
                if (checkpoints is CachingCheckpointStore cachingStore)
                {
                    cachingStore.SetFencingContext(new FencingContext(moduleKey, replicaId, UseProcessorLeaseFencing: true));
                }

                return new LeaseAwareControlLoopGroup(
                    loops, leaseManager, moduleKey, replicaId,
                    sp.GetService<ILogger<LeaseAwareControlLoopGroup>>());
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
            var diMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey);
            var logger = sp.GetService<ILogger<DeadLetterRetryLoop>>();

            // Compose middleware chain (same as ControlLoop)
            var middlewares = new List<ConsumeMiddleware>();
            middlewares.AddRange(diMiddlewares);
            middlewares.AddRange(explicitMiddlewares);
            middlewares.Add(ConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

            var retryLoops = processors
                .Select(p => new DeadLetterRetryLoop(
                    p,
                    deadLetterStore,
                    retryLoopPollingInterval,
                    retryLoopBatchSize,
                    middlewares,
                    logger))
                .ToList();
            return new DeadLetterRetryLoopGroup(retryLoops);
        });
    }
}
