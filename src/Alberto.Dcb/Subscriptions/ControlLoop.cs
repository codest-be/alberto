using System.Threading.Channels;
using Alberto.Dcb.Telemetry;
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
    private readonly IReadOnlyList<BatchConsumeMiddleware> _batchMiddlewares;
    private readonly bool _hasUnpairedPerEventMiddlewares;
    private readonly ProcessorExecutionOptions _executionOptions;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<ControlLoop>? _logger;
    // Pre-composed middleware chains built once at construction time (PERF-6).
    private readonly Func<ConsumeEventContext, Func<Task>, Task> _composedMiddleware;
    private readonly Func<BatchConsumeContext, Func<Task>, Task> _composedBatchMiddleware;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;

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
        IReadOnlyList<BatchConsumeMiddleware>? batchMiddlewares = null,
        bool hasUnpairedPerEventMiddlewares = false,
        ProcessorExecutionOptions? executionOptions = null,
        ILogger<ControlLoop>? logger = null,
        TimeSpan? drainTimeout = null)
    {
        _processor = processor;
        _head = head;
        _backend = backend;
        _checkpointStore = checkpointStore;
        _pollingInterval = pollingInterval;
        _batchSize = batchSize;
        _moduleKey = moduleKey;
        _middlewares = middlewares ?? [];
        _batchMiddlewares = batchMiddlewares ?? [];
        _hasUnpairedPerEventMiddlewares = hasUnpairedPerEventMiddlewares;
        _executionOptions = executionOptions ?? ProcessorExecutionOptions.Default;
        _drainTimeout = drainTimeout ?? Configuration.ControlLoopOptions.Default.DrainTimeout;
        _logger = logger;

        // Pre-build the composed middleware chains once so per-event dispatch does not
        // allocate a recursive Dispatch state-machine stack (PERF-6).
        _composedMiddleware = MiddlewareRunner.Build(_middlewares);
        _composedBatchMiddleware = MiddlewareRunner.Build(_batchMiddlewares);

        // Pipelined mode (MaxConcurrency > 1) uses per-event dispatch with N workers,
        // so it doesn't require IBatchableProcessor or batch middleware.
        if (_executionOptions.MaxConcurrency <= 1)
        {
            if (_executionOptions.BatchingMode == ProcessorBatchingMode.Required &&
                _processor is not IBatchableProcessor)
            {
                throw new InvalidOperationException(
                    $"Processor '{ProcessorId}' requires batching but does not implement {nameof(IBatchableProcessor)}.");
            }

            if (_executionOptions.BatchingMode == ProcessorBatchingMode.Required &&
                _hasUnpairedPerEventMiddlewares)
            {
                throw new InvalidOperationException(
                    $"Processor '{ProcessorId}' requires batching, but not all configured per-event middleware " +
                    "has a batch equivalent. Register matching batch middleware before enabling batching.");
            }
        }

    }

    /// <summary>
    /// Cancels this loop's internal <see cref="CancellationTokenSource"/>.
    /// Called by <see cref="ControlLoopAssembler"/> via a fence-violation subscription
    /// so that a fenced-out replica self-terminates immediately instead of continuing
    /// to dispatch under a stale checkpoint (P0.8).
    /// The <see cref="CancellationTokenSource"/> is read at invocation time so that
    /// subscriptions registered before <see cref="StartAsync"/> are harmless.
    /// </summary>
    internal void Cancel()
    {
        try { Volatile.Read(ref _cts)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the loop and waits for it to drain, bounded by the configured drain timeout.
    /// <para>
    /// A handler that ignores its <see cref="CancellationToken"/> cannot stall shutdown
    /// indefinitely: once the timeout (or <paramref name="cancellationToken"/>) fires the
    /// wait is abandoned and a warning is logged. Abandoning the wait never advances the
    /// checkpoint past an unprocessed event — a worker that never returns also never calls
    /// <c>MarkCompleted</c>, so the safe checkpoint stays behind it and the event is
    /// re-delivered on the next start.
    /// </para>
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        if (_loop is null) return;

        try
        {
            await _loop.WaitAsync(_drainTimeout, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            _logger?.LogWarning(
                "ControlLoop {ProcessorId} did not drain within {DrainTimeout}; abandoning the wait. " +
                "In-flight handlers are still running and were not checkpointed; their events will be " +
                "re-delivered on the next start.",
                ProcessorId, _drainTimeout);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var loop = _loop;
        var abandoned = false;
        CancellationTokenSource? cts = null;

        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            abandoned = loop is not null && !loop.IsCompleted;
            cts = Interlocked.Exchange(ref _cts, null);
        }

        if (abandoned)
        {
            // The loop is still running and its workers still hold tokens from _cts, so
            // neither the CTS nor the processor may be torn down yet. Hand both off to a
            // detached continuation that runs once the loop finally exits.
            _ = ReleaseWhenLoopExitsAsync(loop!, cts, _processor as IAsyncDisposable);
            return;
        }

        cts?.Dispose();
        if (_processor is IAsyncDisposable d) await d.DisposeAsync();
    }

    /// <summary>
    /// Deferred teardown for a loop that outlived its drain timeout: waits (unbounded, off
    /// the shutdown path) for the abandoned loop to exit, then releases the resources its
    /// workers were still using.
    /// </summary>
    private static async Task ReleaseWhenLoopExitsAsync(
        Task loop, CancellationTokenSource? cts, IAsyncDisposable? processor)
    {
        try { await loop.ConfigureAwait(false); }
        catch { /* the loop's own failure is logged where it happens */ }

        cts?.Dispose();

        if (processor is not null)
        {
            try { await processor.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown after an abandoned drain */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger?.LogInformation("ControlLoop {ProcessorId} starting", ProcessorId);

        if (_executionOptions.MaxConcurrency > 1)
        {
            await RunPipelinedAsync(ct);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var checkpoint = await _checkpointStore.GetAsync(ProcessorId, ct) ?? 0L;
                var head = _head.Current;

                if (checkpoint >= head)
                {
                    AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, 0L);
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                var events = await _backend.StreamAllAsync(checkpoint, _batchSize, ct);

                if (events.Count == 0)
                {
                    // No events between checkpoint and head — skip forward safely
                    await _checkpointStore.SaveAsync(ProcessorId, head, ct);
                    AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, 0L);
                    await Task.Delay(_pollingInterval, ct);
                    continue;
                }

                // Single-pass filter: collect visible events and relevant events together
                // to avoid iterating the batch twice (PERF-12).
                var visibleEvents = new List<IEventEnvelope>(events.Count);
                var relevantEvents = new List<IEventEnvelope>(events.Count);
                foreach (var e in events)
                {
                    if (e.GlobalPosition > head) continue;
                    visibleEvents.Add(e);
                    if (_processor.HandledEventTypes.Contains(e.EventType.Id))
                        relevantEvents.Add(e);
                }

                if (ShouldUseBatchDispatch && relevantEvents.Count > 0)
                    await DispatchBatchAsync(relevantEvents, ct);
                else
                    foreach (var evt in relevantEvents)
                        await DispatchAsync(evt, ct);

                var newCheckpoint = visibleEvents.Count > 0
                    ? visibleEvents[visibleEvents.Count - 1].GlobalPosition
                    : checkpoint;

                if (newCheckpoint > checkpoint)
                    await _checkpointStore.SaveAsync(ProcessorId, newCheckpoint, ct);

                AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, head - newCheckpoint);

                // No delay after a full batch — immediately fetch more
                if (events.Count < _batchSize)
                    await Task.Delay(_pollingInterval, ct);
            }
            catch (OperationCanceledException ex) when (IsShutdownCancellation(ex, ct)) { break; }
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

    /// <summary>
    /// Pipelined processing mode: N worker tasks read from a bounded channel concurrently.
    /// The producer keeps reading batches from the event store and feeding matching events
    /// into the channel. Backpressure kicks in when all worker slots are busy.
    /// Checkpoint advances to the highest contiguous completed position (watermark).
    /// </summary>
    private async Task RunPipelinedAsync(CancellationToken ct)
    {
        var maxConcurrency = _executionOptions.MaxConcurrency;
        var initialCheckpoint = await _checkpointStore.GetAsync(ProcessorId, ct) ?? 0L;
        var watermark = new PositionWatermark(initialCheckpoint);
        // Not `using`: when the worker drain times out the abandoned workers still hold this
        // token, so disposal is deferred until they actually exit (see the finally block).
        var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipelineToken = pipelineCts.Token;
        Exception? pipelineFailure = null;

        var channel = Channel.CreateBounded<IEventEnvelope>(
            new BoundedChannelOptions(maxConcurrency)
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // Start worker tasks
        var workers = new Task[maxConcurrency];
        for (var i = 0; i < maxConcurrency; i++)
            workers[i] = RunWorkerAsync(channel.Reader, watermark, ReportFailure, pipelineToken);

        try
        {
            while (!pipelineToken.IsCancellationRequested)
            {
                var head = _head.Current;
                var readPosition = watermark.ReadPosition;

                if (readPosition >= head)
                {
                    // Caught up — flush and wait.
                    // Use SafeCheckpoint, not 0L: the producer may have finished reading, but
                    // workers can still be processing in-flight events; SafeCheckpoint is the
                    // last position all workers have confirmed handled, which is the true lag.
                    await SaveWatermarkCheckpointAsync(watermark, pipelineToken);
                    AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, head - watermark.SafeCheckpoint);
                    await Task.Delay(_pollingInterval, pipelineToken);
                    continue;
                }

                var events = await _backend.StreamAllAsync(readPosition, _batchSize, pipelineToken);

                if (events.Count == 0)
                {
                    watermark.AdvanceReadPosition(head);
                    await SaveWatermarkCheckpointAsync(watermark, pipelineToken);
                    AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, head - watermark.SafeCheckpoint);
                    await Task.Delay(_pollingInterval, pipelineToken);
                    continue;
                }

                var visibleEvents = events
                    .Where(e => e.GlobalPosition <= head)
                    .ToList();

                foreach (var evt in visibleEvents)
                {
                    watermark.AdvanceReadPosition(evt.GlobalPosition);

                    if (_processor.HandledEventTypes.Contains(evt.EventType.Id))
                    {
                        watermark.MarkDispatched(evt.GlobalPosition);
                        // Blocks when all worker slots are busy (backpressure)
                        await channel.Writer.WriteAsync(evt, pipelineToken);
                    }
                }

                // Save checkpoint after each batch of reads
                await SaveWatermarkCheckpointAsync(watermark, pipelineToken);

                AlbertoMetrics.RecordProcessorLag(ProcessorId, _moduleKey, head - watermark.SafeCheckpoint);

                if (events.Count < _batchSize)
                    await Task.Delay(_pollingInterval, pipelineToken);
            }
        }
        catch (OperationCanceledException ex) when (IsShutdownCancellation(ex, pipelineToken))
        {
            // Host shutdown or a worker fault cancelled the complete pipeline.
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            channel.Writer.TryComplete();

            var drain = Task.WhenAll(workers);
            var drained = true;

            try
            {
                await drain.WaitAsync(_drainTimeout);
            }
            catch (TimeoutException)
            {
                drained = false;
            }

            // Final flush. Safe even when the drain timed out: a worker that never returned
            // never called MarkCompleted, so SafeCheckpoint is still behind its position.
            await SaveWatermarkCheckpointAsync(watermark, CancellationToken.None);

            if (drained)
            {
                pipelineCts.Dispose();
            }
            else
            {
                _logger?.LogWarning(
                    "ControlLoop {ProcessorId} abandoned {WorkerCount} worker(s) that did not drain within " +
                    "{DrainTimeout}. Checkpoint flushed at {SafeCheckpoint}; events in flight above that " +
                    "position will be re-delivered on the next start.",
                    ProcessorId, workers.Count(w => !w.IsCompleted), _drainTimeout, watermark.SafeCheckpoint);

                // The abandoned workers still observe pipelineCts.Token — dispose only once
                // they have actually exited.
                _ = DisposeWhenDrainedAsync(drain, pipelineCts);
            }

            if (pipelineFailure is not null)
            {
                IsFaulted = true;
                _logger?.LogCritical(
                    pipelineFailure,
                    "ControlLoop {ProcessorId} pipelined execution faulted and stopped. " +
                    "The failed position remains in flight and will be retried after restart.",
                    ProcessorId);
            }

            _logger?.LogInformation("ControlLoop {ProcessorId} stopped", ProcessorId);
        }

        void ReportFailure(Exception failure)
        {
            if (Interlocked.CompareExchange(ref pipelineFailure, failure, null) is not null)
                return;

            try { pipelineCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Disposes the pipeline's <see cref="CancellationTokenSource"/> once workers abandoned by
    /// a drain timeout have finally exited, so their token registrations stay valid meanwhile.
    /// </summary>
    private static async Task DisposeWhenDrainedAsync(Task drain, CancellationTokenSource cts)
    {
        try { await drain.ConfigureAwait(false); }
        catch { /* worker failures are already reported through ReportFailure */ }

        cts.Dispose();
    }

    private async Task RunWorkerAsync(
        ChannelReader<IEventEnvelope> reader,
        PositionWatermark watermark,
        Action<Exception> reportFailure,
        CancellationToken ct)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                try
                {
                    await DispatchAsync(evt, ct);
                }
                catch (OperationCanceledException ex) when (IsShutdownCancellation(ex, ct))
                {
                    // Shutdown cancellation: leave this position in-flight so the
                    // watermark checkpoint does not advance past an unprocessed event.
                    // The event will be re-processed after restart (at-least-once).
                    break;
                }
                catch (Exception ex)
                {
                    // Middleware (retry + dead-letter) owns policy-handled failures. Anything
                    // escaping that interface is unrecoverable: leave the position in flight
                    // and stop the whole pipeline so restart redelivers it.
                    _logger?.LogError(ex,
                        "ControlLoop {ProcessorId} worker: unhandled error at position {Position}",
                        ProcessorId, evt.GlobalPosition);
                    reportFailure(ex);
                    break;
                }
                // Only mark completed when dispatch actually finished (success or handled
                // failure). Cancelled events must NOT be marked — the position stays
                // in-flight so SaveWatermarkCheckpointAsync won't advance past it (COR-1).
                watermark.MarkCompleted(evt.GlobalPosition);
            }
        }
        // Deliberately not IsShutdownCancellation: this arm only ever sees cancellation from
        // ReadAllAsync, never processor code. Narrowing it would let an OCE escape into the
        // Task.WhenAll(workers) inside RunPipelinedAsync's finally, throwing from the block
        // that records the fault instead of being recorded by it.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
    }

    /// <summary>
    /// Tells a genuine shutdown apart from an <see cref="OperationCanceledException"/> that
    /// merely coincided with one.
    /// </summary>
    /// <remarks>
    /// Cooperative cancellation always carries the token that caused it. An OCE that carries
    /// no cancelled token — a processor throwing <c>new OperationCanceledException()</c>, a
    /// <c>TaskCompletionSource</c> cancelled without a token — is an escaped failure, not a
    /// clean stop. Testing only <c>ct.IsCancellationRequested</c> conflates the two, so any
    /// handler that failed at the same moment the host shut down was reported as a graceful
    /// stop and <see cref="IsFaulted"/> silently stayed <c>false</c>.
    /// </remarks>
    private static bool IsShutdownCancellation(OperationCanceledException exception, CancellationToken ct)
        => ct.IsCancellationRequested && exception.CancellationToken.IsCancellationRequested;

    private async Task SaveWatermarkCheckpointAsync(PositionWatermark watermark, CancellationToken ct)
    {
        var safeCheckpoint = watermark.SafeCheckpoint;
        var current = await _checkpointStore.GetAsync(ProcessorId, ct) ?? 0L;
        if (safeCheckpoint > current)
            await _checkpointStore.SaveAsync(ProcessorId, safeCheckpoint, ct);
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

        return _composedMiddleware(context, () => _processor.ProcessEventAsync(evt, ct));
    }

    private Task DispatchBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct)
        => DispatchBatchAsync(events, ct, allowSplit: true);

    private async Task DispatchBatchAsync(
        IReadOnlyList<IEventEnvelope> events,
        CancellationToken ct,
        bool allowSplit)
    {
        if (_processor is not IBatchableProcessor batchableProcessor)
        {
            throw new InvalidOperationException(
                $"Processor '{ProcessorId}' was configured for batching but does not implement {nameof(IBatchableProcessor)}.");
        }

        var context = new BatchConsumeContext
        {
            ProcessorId = _processor.ProcessorId,
            ModuleKey = _moduleKey,
            Envelopes = events,
            IsRebuild = _processor.IsRebuilding,
            CancellationToken = ct,
        };

        try
        {
            await _composedBatchMiddleware(context, () => batchableProcessor.ProcessBatchAsync(events, ct));
        }
        catch (Exception ex) when (allowSplit && events.Count > 1 && ex is not OperationCanceledException)
        {
            var midpoint = events.Count / 2;
            await DispatchBatchAsync(events.Take(midpoint).ToArray(), ct, allowSplit: true);
            await DispatchBatchAsync(events.Skip(midpoint).ToArray(), ct, allowSplit: true);
        }
    }

    private bool ShouldUseBatchDispatch =>
        _executionOptions.BatchingMode != ProcessorBatchingMode.Disabled &&
        !_hasUnpairedPerEventMiddlewares &&
        _processor is IBatchableProcessor;
}
