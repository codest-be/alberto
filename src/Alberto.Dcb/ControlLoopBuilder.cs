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

    internal ControlLoopBuilder(DcbModuleBuilder moduleBuilder) =>
        _moduleBuilder = moduleBuilder;

    public ControlLoopBuilder WithPollingInterval(TimeSpan interval)
    { _pollingInterval = interval; return this; }

    public ControlLoopBuilder WithBatchSize(int batchSize)
    { _batchSize = batchSize; return this; }

    public ControlLoopBuilder WithHeadRefreshInterval(TimeSpan interval)
    { _headRefreshInterval = interval; return this; }

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
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);

            var middlewares = new List<ConsumeMiddleware>();
            middlewares.AddRange(diMiddlewares);
            middlewares.AddRange(explicitMiddlewares);
            middlewares.Add(ConsumeMiddlewares.RetryAndDeadLetter(errorPolicy, deadLetterStore));

            var loops = processors
                .Select(p => new ControlLoop(p, head, backend, checkpoints,
                    pollingInterval, batchSize, moduleKey, middlewares, logger))
                .ToList();
            return new ControlLoopGroup(loops);
        });
    }
}
