using Alberto.Dcb.Subscriptions;
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
    /// Registers an event processor to handle events.
    /// </summary>
    /// <typeparam name="TProcessor">The processor type implementing <see cref="IEventProcessor"/>.</typeparam>
    public ConsumerBuilder AddProcessor<TProcessor>() where TProcessor : class, IEventProcessor
    {
        _processorTypes.Add(typeof(TProcessor));
        _moduleBuilder.Services.AddKeyedScoped<IEventProcessor, TProcessor>(_moduleBuilder.ModuleKey);
        return this;
    }

    internal void Build()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;

        // Register PollingConsumer
        _moduleBuilder.Services.AddKeyedSingleton<PollingConsumer>(moduleKey, (sp, _) =>
        {
            var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpointStore = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);

            var consumer = new PollingConsumer(
                backend,
                checkpointStore,
                $"{moduleKey}-consumer",
                pollingInterval,
                batchSize);

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
