using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Subscriptions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb;

/// <summary>
/// Builder for configuring a polling consumer with event processors.
/// </summary>
public sealed class ConsumerBuilder
{
    private readonly DcbModuleBuilder _moduleBuilder;
    private TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(100);
    private int _batchSize = 100;
    private int _rebuildBatchSize = 1000;
    private long _rebuildThreshold = 1000;
    private ErrorPolicy _errorPolicy = ErrorPolicy.Default;
    private ConsumerDistributionMode _distributionMode = ConsumerDistributionMode.SingleLeader;
    private TimeSpan _tenantLockRetryInterval = TimeSpan.FromSeconds(30);
    private bool _enableProcessorLock = true; // Single-leader lock enabled by default
    private int _maxParallelProjections = 1; // Default sequential - EF projections aren't thread-safe
    private int? _maxTenantsPerReplica; // null = unlimited
    private TimeSpan? _handlerTimeout = TimeSpan.FromSeconds(30);
    private Action<string, IEventEnvelope>? _onProjected;
    private Action<string>? _onRebuildComplete;
    private readonly List<ConsumeMiddleware> _middlewares = [];
    private ITenantRing? _tenantRing;

    internal ConsumerBuilder(DcbModuleBuilder moduleBuilder)
    {
        _moduleBuilder = moduleBuilder;
    }

    /// <summary>
    /// Gets the service collection for registering additional services.
    /// </summary>
    internal IServiceCollection Services => _moduleBuilder.Services;

    /// <summary>
    /// Gets the module key for keyed service registration.
    /// </summary>
    internal string ModuleKey => _moduleBuilder.ModuleKey;

    /// <summary>
    /// Sets the polling interval for checking new events.
    /// </summary>
    public ConsumerBuilder WithPollingInterval(TimeSpan interval)
    {
        _pollingInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of events to fetch per poll.
    /// </summary>
    public ConsumerBuilder WithBatchSize(int batchSize)
    {
        _batchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Sets the batch size used when rebuilding processors that are far behind.
    /// Larger batches allow faster catch-up. Default is 1000.
    /// </summary>
    public ConsumerBuilder WithRebuildBatchSize(int rebuildBatchSize)
    {
        _rebuildBatchSize = rebuildBatchSize;
        return this;
    }

    /// <summary>
    /// Sets the position lag threshold that triggers rebuild mode.
    /// Processors lagging more than this threshold will run independently.
    /// Default is 1000 events.
    /// </summary>
    public ConsumerBuilder WithRebuildThreshold(long rebuildThreshold)
    {
        _rebuildThreshold = rebuildThreshold;
        return this;
    }

    /// <summary>
    /// Configures the error handling policy.
    /// </summary>
    public ConsumerBuilder WithErrorPolicy(ErrorPolicy policy)
    {
        _errorPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Configures the error handling policy.
    /// </summary>
    public ConsumerBuilder WithErrorPolicy(Action<ErrorPolicyBuilder> configure)
    {
        var builder = new ErrorPolicyBuilder();
        configure(builder);
        _errorPolicy = builder.Build();
        return this;
    }

    /// <summary>
    /// Explicitly sets single-leader mode (this is the default).
    /// Only one instance will process events at a time. Other instances wait as standby.
    /// </summary>
    public ConsumerBuilder WithSingleLeaderLock()
    {
        _distributionMode = ConsumerDistributionMode.SingleLeader;
        _enableProcessorLock = true;
        return this;
    }

    /// <summary>
    /// Enables tenant-distributed mode where multiple instances each claim different tenants.
    /// Events are processed by the instance that holds the lock for each tenant.
    /// </summary>
    /// <param name="retryInterval">Interval between attempts to acquire locks for unowned tenants. Default is 30 seconds.</param>
    public ConsumerBuilder WithTenantDistribution(TimeSpan? retryInterval = null)
    {
        _distributionMode = ConsumerDistributionMode.TenantDistributed;
        _tenantLockRetryInterval = retryInterval ?? TimeSpan.FromSeconds(30);
        return this;
    }

    /// <summary>
    /// Sets the maximum number of tenants a single replica can own.
    /// When set, replicas will stop claiming new tenants after reaching this limit,
    /// ensuring more even distribution across replicas.
    /// </summary>
    /// <param name="maxTenants">Maximum tenants per replica. Default is unlimited (null).</param>
    public ConsumerBuilder WithMaxTenantsPerReplica(int maxTenants)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTenants);
        _maxTenantsPerReplica = maxTenants;
        return this;
    }

    /// <summary>
    /// Enables parallel processing of projections within each batch.
    /// Multiple projections can process events concurrently, improving throughput.
    /// </summary>
    /// <param name="maxDegree">Maximum number of projections to process in parallel. Default is unlimited.</param>
    public ConsumerBuilder WithParallelProjections(int maxDegree = int.MaxValue)
    {
        _maxParallelProjections = maxDegree;
        return this;
    }

    /// <summary>
    /// Sets a per-handler timeout. If an individual event handler takes longer than this duration,
    /// its token is cancelled and the failure is handed to the error policy (retry + dead-letter).
    /// Has no effect on graceful consumer shutdown.
    /// Pass <c>null</c> to disable the timeout entirely (e.g. for AI handlers with long-running calls).
    /// Default is 30 seconds.
    /// </summary>
    public ConsumerBuilder WithHandlerTimeout(TimeSpan? timeout)
    {
        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Handler timeout must be positive.");
        _handlerTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Registers a callback to be invoked after each event is successfully processed by a processor.
    /// Use for test observability. Do not perform heavy work in this callback.
    /// </summary>
    public ConsumerBuilder OnProjected(Action<string, IEventEnvelope> callback)
    {
        _onProjected = callback;
        return this;
    }

    /// <summary>
    /// Registers a callback to be invoked when a processor completes a rebuild.
    /// </summary>
    public ConsumerBuilder OnRebuildComplete(Action<string> callback)
    {
        _onRebuildComplete = callback;
        return this;
    }

    /// <summary>
    /// Configures a consistent hash ring for deterministic tenant assignment across replicas.
    /// When set, tenants are assigned to specific replicas based on a hash of the tenant ID,
    /// ensuring stable ownership and minimal tenant movement when the replica set changes.
    /// </summary>
    public ConsumerBuilder WithConsistentHashRing(ITenantRing tenantRing)
    {
        ArgumentNullException.ThrowIfNull(tenantRing);
        _tenantRing = tenantRing;
        return this;
    }

    /// <summary>
    /// Adds a middleware to the per-event consume pipeline.
    /// Middlewares are executed in registration order (first registered = outermost wrapper).
    /// When at least one middleware is registered, the default RetryAndDeadLetter middleware
    /// is not added automatically — include it explicitly if needed.
    /// </summary>
    public ConsumerBuilder WithMiddleware(ConsumeMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Registers an async projection processor with a state store factory.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStoreFactory">Factory to create a state store.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    [Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
    public ConsumerBuilder AddProjection<TState, TProjection>(
        Func<IStateStore<TState>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
#pragma warning disable CS0618
        var id = processorId ?? typeof(TProjection).Name;
        var processor = new AsyncProjection<TState, TProjection>(stateStoreFactory, id);
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, processor);
#pragma warning restore CS0618
        return this;
    }

    /// <summary>
    /// Registers an async projection processor with a state store factory.
    /// Use this overload when the factory needs access to the service provider.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStoreFactory">Factory to create a state store, with access to services.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    [Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
    public ConsumerBuilder AddProjection<TState, TProjection>(
        Func<IServiceProvider, Func<IStateStore<TState>>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
#pragma warning disable CS0618
        var id = processorId ?? typeof(TProjection).Name;
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, (sp, _) =>
        {
            var tenantStoreFactory = stateStoreFactory(sp);
            return new AsyncProjection<TState, TProjection>(tenantStoreFactory, id);
        });
#pragma warning restore CS0618
        return this;
    }

    /// <summary>
    /// Registers an async projection processor using the declaration-based API.
    /// This is the recommended approach — no reflection, no base classes required.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <param name="declaration">
    /// A <see cref="ProjectionDeclaration{TState}"/> produced by
    /// <see cref="DeclareProjection.For{TState}"/>.
    /// </param>
    /// <param name="stateStoreFactory">
    /// A delegate that, given the service provider, returns a state store factory.
    /// </param>
    public ConsumerBuilder AddProjection<TState>(
        ProjectionDeclaration<TState> declaration,
        Func<IServiceProvider, Func<IStateStore<TState>>> stateStoreFactory)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, (sp, _) =>
        {
            var factory = stateStoreFactory(sp);
            return new DeclaredAsyncProjection<TState>(declaration, factory);
        });
        return this;
    }

    /// <summary>
    /// Adds a consume filter to the pipeline.
    /// </summary>
    /// <remarks>
    /// Deprecated. Use <see cref="WithMiddleware"/> to add per-event middleware instead.
    /// </remarks>
#pragma warning disable CS0618
    [Obsolete("Use WithMiddleware instead. AddFilter will be removed in a future version.")]
    public ConsumerBuilder AddFilter<TFilter>() where TFilter : class, IConsumeFilter
    {
        _moduleBuilder.Services.AddKeyedSingleton<IConsumeFilter, TFilter>(_moduleBuilder.ModuleKey);
        return this;
    }
#pragma warning restore CS0618

    internal void Build()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;
        var rebuildBatchSize = _rebuildBatchSize;
        var rebuildThreshold = _rebuildThreshold;
        var errorPolicy = _errorPolicy;
        var distributionMode = _distributionMode;
        var tenantLockRetryInterval = _tenantLockRetryInterval;
        var enableProcessorLock = _enableProcessorLock;
        var maxParallelProjections = _maxParallelProjections;
        var maxTenantsPerReplica = _maxTenantsPerReplica;
        var onProjected = _onProjected;
        var onRebuildComplete = _onRebuildComplete;
        var builderMiddlewares = _middlewares.ToList();
        var tenantRing = _tenantRing;
        var handlerTimeout = _handlerTimeout;

#pragma warning disable CS0618
        // Register pipeline (legacy IConsumeFilter support — transitional)
        _moduleBuilder.Services.AddKeyedSingleton<IConsumeFilterPipeline>(moduleKey, (sp, _) =>
        {
            var filters = sp.GetKeyedServices<IConsumeFilter>(moduleKey);
            return new ConsumeFilterPipeline(filters);
        });
#pragma warning restore CS0618

        // Register PollingConsumer
        _moduleBuilder.Services.AddKeyedSingleton<PollingConsumer>(moduleKey, (sp, _) =>
        {
            // In tenant mode, a singleton ":consumer" backend is registered that skips the per-request
            // tenant decorator (the consumer streams all events across tenants). Fall back to the
            // standard key for single-tenant mode where IEventStoreBackend is already a singleton.
            var backend = sp.GetKeyedService<IEventStoreBackend>(moduleKey + ":consumer")
                         ?? sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);
#pragma warning disable CS0618
            var pipeline = sp.GetKeyedService<IConsumeFilterPipeline>(moduleKey);
#pragma warning restore CS0618
            var logger = sp.GetService<ILogger<PollingConsumer>>();

            // Resolve locks based on distribution mode
            IProcessorLock? processorLock = null;
            ITenantProcessorLock? tenantProcessorLock = null;

            if (distributionMode == ConsumerDistributionMode.TenantDistributed)
            {
                tenantProcessorLock = sp.GetKeyedService<ITenantProcessorLock>(moduleKey);
            }
            else if (enableProcessorLock)
            {
                processorLock = sp.GetKeyedService<IProcessorLock>(moduleKey);
            }

            // Merge builder-configured middlewares with any keyed ConsumeMiddleware DI services.
            // Keyed services are placed outermost (first), builder middlewares innermost.
            // If only keyed services are added, RetryAndDeadLetter is included as the default inner layer.
            var keyedMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList();
            IReadOnlyList<ConsumeMiddleware>? effectiveMiddlewares;
            if (keyedMiddlewares.Count > 0)
            {
                var innerMiddlewares = builderMiddlewares.Count > 0
                    ? (IReadOnlyList<ConsumeMiddleware>)builderMiddlewares
                    : [ConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore)];
                effectiveMiddlewares = [.. keyedMiddlewares, .. innerMiddlewares];
            }
            else
            {
                effectiveMiddlewares = builderMiddlewares.Count > 0 ? builderMiddlewares : null;
            }

            var consumer = new PollingConsumer(
                backend,
                checkpointStore,
                $"{moduleKey}-consumer",
                moduleKey,
                pollingInterval,
                batchSize,
                processorLock,
                deadLetterStore,
                pipeline,
                errorPolicy,
                rebuildBatchSize,
                rebuildThreshold,
                tenantProcessorLock,
                distributionMode,
                tenantLockRetryInterval,
                maxParallelProjections,
                maxTenantsPerReplica,
                logger,
                effectiveMiddlewares,
                handlerTimeout);

            // Register all processors
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey);
            foreach (var processor in processors)
            {
                consumer.RegisterProcessor(processor);
            }

            consumer.OnProjected = onProjected;
            consumer.OnRebuildComplete = onRebuildComplete;
            consumer.TenantRing = tenantRing;

            return consumer;
        });

        // Register hosted service
        // Note: We use AddSingleton<IHostedService> instead of AddHostedService because
        // AddHostedService uses TryAddEnumerable which skips duplicates of the same
        // implementation type. This allows multiple modules to each have their own consumer.
        _moduleBuilder.Services.AddSingleton<IHostedService>(sp =>
        {
            var consumer = sp.GetRequiredKeyedService<PollingConsumer>(moduleKey);
            return new PollingConsumerHostedService(consumer);
        });
    }
}

/// <summary>
/// Builder for configuring error policy.
/// </summary>
public sealed class ErrorPolicyBuilder
{
    private int _maxRetries = 3;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    private double _backoffMultiplier = 2.0;
    private TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(30);
    private bool _deadLetterOnMaxRetries = true;
    private IErrorClassifier _errorClassifier = DefaultErrorClassifier.Instance;

    /// <summary>
    /// Sets the maximum number of retry attempts.
    /// </summary>
    public ErrorPolicyBuilder MaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Sets the base delay between retry attempts (first retry).
    /// </summary>
    public ErrorPolicyBuilder RetryDelay(TimeSpan delay)
    {
        _retryDelay = delay;
        return this;
    }

    /// <summary>
    /// Sets the multiplier for exponential backoff between retries.
    /// Set to 1.0 for constant delay. Default is 2.0.
    /// </summary>
    public ErrorPolicyBuilder BackoffMultiplier(double multiplier)
    {
        _backoffMultiplier = multiplier;
        return this;
    }

    /// <summary>
    /// Sets the maximum delay between retries (cap for exponential backoff).
    /// Default is 30 seconds.
    /// </summary>
    public ErrorPolicyBuilder MaxRetryDelay(TimeSpan maxDelay)
    {
        _maxRetryDelay = maxDelay;
        return this;
    }

    /// <summary>
    /// Sets whether to dead-letter events after max retries.
    /// </summary>
    public ErrorPolicyBuilder DeadLetterOnMaxRetries(bool deadLetter)
    {
        _deadLetterOnMaxRetries = deadLetter;
        return this;
    }

    /// <summary>
    /// Sets a custom error classifier for determining transient vs permanent errors.
    /// </summary>
    public ErrorPolicyBuilder UseErrorClassifier(IErrorClassifier classifier)
    {
        _errorClassifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        return this;
    }

    /// <summary>
    /// Uses the default error classifier.
    /// </summary>
    public ErrorPolicyBuilder UseDefaultErrorClassifier()
    {
        _errorClassifier = DefaultErrorClassifier.Instance;
        return this;
    }

    internal ErrorPolicy Build() => new()
    {
        MaxRetries = _maxRetries,
        RetryDelay = _retryDelay,
        BackoffMultiplier = _backoffMultiplier,
        MaxRetryDelay = _maxRetryDelay,
        DeadLetterOnMaxRetries = _deadLetterOnMaxRetries,
        ErrorClassifier = _errorClassifier
    };
}
