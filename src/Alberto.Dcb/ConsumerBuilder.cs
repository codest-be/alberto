using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Subscriptions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb;

/// <summary>
/// Builder for configuring a polling consumer with event processors.
/// </summary>
public sealed class ConsumerBuilder
{
    private readonly DcbModuleBuilder _moduleBuilder;
    private readonly List<Type> _processorTypes = [];
    private TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(100);
    private int _batchSize = 100;
    private int _rebuildBatchSize = 1000;
    private long _rebuildThreshold = 1000;
    private ErrorPolicy _errorPolicy = ErrorPolicy.Default;

    internal ConsumerBuilder(DcbModuleBuilder moduleBuilder)
    {
        _moduleBuilder = moduleBuilder;
    }

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
    /// Registers an event processor to handle events.
    /// </summary>
    /// <typeparam name="TProcessor">The processor type implementing <see cref="IEventProcessor"/>.</typeparam>
    public ConsumerBuilder AddProcessor<TProcessor>() where TProcessor : class, IEventProcessor
    {
        _processorTypes.Add(typeof(TProcessor));
        _moduleBuilder.Services.AddKeyedScoped<IEventProcessor, TProcessor>(_moduleBuilder.ModuleKey);
        return this;
    }

    /// <summary>
    /// Registers an async projection processor with a tenant-aware state store factory.
    /// The factory is called with the tenant ID from each event being processed.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStoreFactory">Factory to create a state store for a given tenant ID.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    public ConsumerBuilder AddProjection<TState, TProjection>(
        Func<string, IStateStore<TState>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        var id = processorId ?? typeof(TProjection).Name;
        var processor = new AsyncProjection<TState, TProjection>(stateStoreFactory, id);
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, processor);
        return this;
    }

    /// <summary>
    /// Registers an async projection processor with a tenant-aware state store factory.
    /// Use this overload when the factory needs access to the service provider.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStoreFactory">Factory to create a state store for a given tenant ID, with access to services.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    public ConsumerBuilder AddProjection<TState, TProjection>(
        Func<IServiceProvider, Func<string, IStateStore<TState>>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        var id = processorId ?? typeof(TProjection).Name;
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, (sp, _) =>
        {
            var tenantStoreFactory = stateStoreFactory(sp);
            return new AsyncProjection<TState, TProjection>(tenantStoreFactory, id);
        });
        return this;
    }

    /// <summary>
    /// Adds a consume filter to the pipeline.
    /// </summary>
    public ConsumerBuilder AddFilter<TFilter>() where TFilter : class, IConsumeFilter
    {
        _moduleBuilder.Services.AddKeyedSingleton<IConsumeFilter, TFilter>(_moduleBuilder.ModuleKey);
        return this;
    }

    internal void Build()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;
        var rebuildBatchSize = _rebuildBatchSize;
        var rebuildThreshold = _rebuildThreshold;
        var errorPolicy = _errorPolicy;

        // Register pipeline
        _moduleBuilder.Services.AddKeyedSingleton<IConsumeFilterPipeline>(moduleKey, (sp, _) =>
        {
            var filters = sp.GetKeyedServices<IConsumeFilter>(moduleKey);
            return new ConsumeFilterPipeline(filters);
        });

        // Register PollingConsumer
        _moduleBuilder.Services.AddKeyedSingleton<PollingConsumer>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);
            var pipeline = sp.GetKeyedService<IConsumeFilterPipeline>(moduleKey);

            var consumer = new PollingConsumer(
                backend,
                checkpointStore,
                $"{moduleKey}-consumer",
                moduleKey,
                pollingInterval,
                batchSize,
                processorLock: null,
                deadLetterStore,
                pipeline,
                errorPolicy,
                rebuildBatchSize,
                rebuildThreshold);

            // Register all processors
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey);
            foreach (var processor in processors)
            {
                consumer.RegisterProcessor(processor);
            }

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
