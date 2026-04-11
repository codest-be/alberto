using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Runs a single IEventProcessor as an independent hosted service.
/// Polls from its own checkpoint up to EventStoreHead.Current.
/// Each event is dispatched through a <see cref="ConsumeMiddleware"/> chain
/// (retry, dead-letter, telemetry, ...) before reaching the processor, so a
/// single bad event cannot halt the loop.
/// On unrecoverable failures (errors that escape the middleware chain): stops,
/// logs Critical, preserves checkpoint for retry on restart.
/// </summary>
public sealed class ControlLoop : IHostedService, IAsyncDisposable
{
    private readonly IEventProcessor _processor;
    private readonly EventStoreHead _head;
    private readonly IEventStoreBackend _backend;
    private readonly ICheckpointStore _checkpointStore;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly string _moduleKey;
    private readonly IReadOnlyList<ConsumeMiddleware> _middlewares;
    private readonly ILogger<ControlLoop>? _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsFaulted { get; private set; }
    public string ProcessorId => _processor.ProcessorId;

    internal ControlLoop(
        IEventProcessor processor,
        EventStoreHead head,
        IEventStoreBackend backend,
        ICheckpointStore checkpointStore,
        TimeSpan pollingInterval,
        int batchSize,
        string moduleKey = "",
        IReadOnlyList<ConsumeMiddleware>? middlewares = null,
        ILogger<ControlLoop>? logger = null)
    {
        _processor = processor;
        _head = head;
        _backend = backend;
        _checkpointStore = checkpointStore;
        _pollingInterval = pollingInterval;
        _batchSize = batchSize;
        _moduleKey = moduleKey;
        _middlewares = middlewares ?? [];
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_processor is IAsyncDisposable d) await d.DisposeAsync();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger?.LogInformation("ControlLoop {ProcessorId} starting", ProcessorId);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var checkpoint = await _checkpointStore.GetAsync(ProcessorId, ct) ?? 0L;
                var head = _head.Current;

                if (checkpoint >= head)
                {
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                var events = await _backend.StreamAll(checkpoint, _batchSize, ct);

                if (events.Count == 0)
                {
                    // No events between checkpoint and head — skip forward safely
                    await _checkpointStore.SaveAsync(ProcessorId, head, ct);
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                foreach (var evt in events.Where(e => e.GlobalPosition <= head))
                {
                    if (!_processor.HandledEventTypes.Contains(evt.EventType.Id))
                        continue;

                    await DispatchAsync(evt, ct);
                }

                var newCheckpoint = events
                    .Where(e => e.GlobalPosition <= head)
                    .Select(e => e.GlobalPosition)
                    .DefaultIfEmpty(checkpoint)
                    .Max();

                if (newCheckpoint > checkpoint)
                    await _checkpointStore.SaveAsync(ProcessorId, newCheckpoint, ct);

                // No delay after a full batch — immediately fetch more
                if (events.Count < _batchSize)
                    await Task.Delay(_pollingInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                IsFaulted = true;
                _logger?.LogCritical(ex,
                    "ControlLoop {ProcessorId} faulted and stopped. " +
                    "Checkpoint NOT advanced. Restart the service to retry from the same position.",
                    ProcessorId);
                return;
            }
        }
        _logger?.LogInformation("ControlLoop {ProcessorId} stopped", ProcessorId);
    }

    private Task DispatchAsync(IEventEnvelope evt, CancellationToken ct)
    {
        if (_middlewares.Count == 0)
            return _processor.ProcessEventAsync(evt, ct);

        var context = new ConsumeEventContext
        {
            ProcessorId = _processor.ProcessorId,
            ModuleKey = _moduleKey,
            Envelope = evt,
            IsRebuild = _processor.IsRebuilding,
            CancellationToken = ct,
        };

        return MiddlewareRunner.RunAsync(
            context,
            _middlewares,
            () => _processor.ProcessEventAsync(evt, ct));
    }
}
