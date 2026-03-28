using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb;

public sealed class ControlLoopBuilder
{
    private readonly DcbModuleBuilder _moduleBuilder;
    private TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(250);
    private int _batchSize = 100;
    private TimeSpan _headRefreshInterval = TimeSpan.FromMilliseconds(100);
    private int _headWindowSize = 2000;

    internal ControlLoopBuilder(DcbModuleBuilder moduleBuilder) =>
        _moduleBuilder = moduleBuilder;

    public ControlLoopBuilder WithPollingInterval(TimeSpan interval)
    { _pollingInterval = interval; return this; }

    public ControlLoopBuilder WithBatchSize(int batchSize)
    { _batchSize = batchSize; return this; }

    public ControlLoopBuilder WithHeadRefreshInterval(TimeSpan interval)
    { _headRefreshInterval = interval; return this; }

    internal void Build()
    {
        var moduleKey = _moduleBuilder.ModuleKey;
        var pollingInterval = _pollingInterval;
        var batchSize = _batchSize;
        var headRefreshInterval = _headRefreshInterval;
        var headWindowSize = _headWindowSize;
        var services = _moduleBuilder.Services;

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
            var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
            var backend = sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
                         ?? sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey);
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var logger = sp.GetService<ILogger<ControlLoop>>();

            var loops = processors
                .Select(p => new ControlLoop(p, head, backend, checkpoints,
                    pollingInterval, batchSize, logger))
                .ToList();
            return new ControlLoopGroup(loops);
        });
    }
}
