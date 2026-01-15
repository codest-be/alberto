using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Subscriptions.Pipeline;
using Microsoft.Extensions.DependencyInjection;

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
    /// Registers an async projection processor with its state store.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStore">The state store for persisting projection state.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    public ConsumerBuilder AddProjection<TState, TProjection>(
        IStateStore<TState> stateStore,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        var id = processorId ?? typeof(TProjection).Name;
        var processor = new AsyncProjection<TState, TProjection>(stateStore, id);
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, processor);
        return this;
    }

    /// <summary>
    /// Registers an async projection processor with a state store factory.
    /// Use this overload when the state store needs to be resolved from the service provider.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <param name="stateStoreFactory">Factory to create the state store.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    public ConsumerBuilder AddProjection<TState, TProjection>(
        Func<IServiceProvider, IStateStore<TState>> stateStoreFactory,
        string? processorId = null)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        var id = processorId ?? typeof(TProjection).Name;
        _moduleBuilder.Services.AddKeyedSingleton<IEventProcessor>(_moduleBuilder.ModuleKey, (sp, _) =>
        {
            var stateStore = stateStoreFactory(sp);
            return new AsyncProjection<TState, TProjection>(stateStore, id);
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
                errorPolicy);

            // Register all processors
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey);
            foreach (var processor in processors)
            {
                consumer.RegisterProcessor(processor);
            }

            return consumer;
        });

        // Register hosted service
        _moduleBuilder.Services.AddHostedService(sp =>
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
    private bool _deadLetterOnMaxRetries = true;

    /// <summary>
    /// Sets the maximum number of retry attempts.
    /// </summary>
    public ErrorPolicyBuilder MaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Sets the delay between retry attempts.
    /// </summary>
    public ErrorPolicyBuilder RetryDelay(TimeSpan delay)
    {
        _retryDelay = delay;
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

    internal ErrorPolicy Build() => new()
    {
        MaxRetries = _maxRetries,
        RetryDelay = _retryDelay,
        DeadLetterOnMaxRetries = _deadLetterOnMaxRetries
    };
}
