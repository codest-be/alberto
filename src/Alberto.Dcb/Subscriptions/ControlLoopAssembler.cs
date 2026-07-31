using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Assembles a fully-configured <see cref="ControlLoop"/> from module-level and
/// processor-level concerns, so both the live-loop path and the shadow-rebuild path
/// go through a single composition seam.
/// </summary>
/// <remarks>
/// <para>
/// The assembler is shallow: it collects the module-wide middleware lists once in its
/// constructor and delegates per-processor concerns (checkpoint store, execution options,
/// logger) to <see cref="Create"/>. This keeps the interface small and makes it obvious
/// which parameters vary across loops and which are shared.
/// </para>
/// <para>
/// <b>Why this exists</b>: before the assembler, two call sites — the live-loop factory
/// in <c>ControlLoopRegistration</c> and the shadow-rebuild factory in
/// <c>DcbModuleBuilderExtensions</c> — independently composed middleware lists,
/// computed <c>hasUnpairedPerEventMiddlewares</c>, and called the <see cref="ControlLoop"/>
/// constructor. A drift between those two sites would silently cause a rebuild to replay
/// under different middleware than the live loop. The assembler is the single place where
/// that composition lives; both call sites now call <see cref="Create"/> and cannot drift.
/// </para>
/// <para>
/// <b>Fence subscription</b>: if the checkpoint store supplied to <see cref="Create"/>
/// implements <see cref="IFencableCheckpointStore"/>, the assembler wires a per-loop
/// fence-violation handler that cancels only that loop's internal
/// <see cref="CancellationTokenSource"/>. This replaces the previous concrete-type
/// downcast in <see cref="ControlLoop"/>'s constructor, so any adapter that wraps
/// <see cref="CachingCheckpointStore"/> and also implements
/// <see cref="IFencableCheckpointStore"/> is reached correctly.
/// </para>
/// </remarks>
internal sealed class ControlLoopAssembler
{
    private readonly IReadOnlyList<ConsumeMiddleware> _middlewares;
    private readonly IReadOnlyList<BatchConsumeMiddleware> _batchMiddlewares;
    private readonly bool _hasUnpairedPerEventMiddlewares;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Builds the shared middleware chain that every loop created by this assembler will use.
    /// </summary>
    /// <param name="diMiddlewares">Per-event middlewares resolved from DI (outermost first).</param>
    /// <param name="diBatchMiddlewares">Batch middlewares resolved from DI (outermost first).</param>
    /// <param name="retryOptions">Retry policy for the innermost RetryAndDeadLetter middleware.</param>
    /// <param name="classifier">Error classifier used by RetryAndDeadLetter.</param>
    /// <param name="deadLetterStore">Dead-letter store, or null if none is registered.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.FailedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">
    /// Module-scoped logger for the dead-letter middleware. When a dead-letter write cannot be
    /// made durable the entry is dropped so the checkpoint can still advance, and this log is the
    /// only remaining trace that it happened — without it the drop is silent. Optional so tests
    /// can construct an assembler without a logging stack, but production paths must supply one.
    /// </param>
    public ControlLoopAssembler(
        IReadOnlyList<ConsumeMiddleware> diMiddlewares,
        IReadOnlyList<BatchConsumeMiddleware> diBatchMiddlewares,
        RetryOptions retryOptions,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Middleware order (outermost first):
        //   [keyed ConsumeMiddleware...]   ← WithTelemetry(), AddConsumeMiddleware(...)
        //   RetryAndDeadLetter             ← always innermost
        //   processor.ProcessEventAsync    ← terminal
        var middlewares = new List<ConsumeMiddleware>(diMiddlewares)
        {
            ConsumeMiddlewares.RetryAndDeadLetter(retryOptions, classifier, deadLetterStore, _timeProvider, logger),
        };
        var batchMiddlewares = new List<BatchConsumeMiddleware>(diBatchMiddlewares)
        {
            BatchConsumeMiddlewares.RetryAndDeadLetter(retryOptions, classifier, deadLetterStore, _timeProvider, logger),
        };

        // A per-event middleware with no batch counterpart cannot be honoured on the batch
        // path, so batching falls back to per-event dispatch rather than silently skipping it.
        // This is a derived fact — callers must not compute it independently.
        _hasUnpairedPerEventMiddlewares = diMiddlewares.Count > diBatchMiddlewares.Count;
        _middlewares = middlewares;
        _batchMiddlewares = batchMiddlewares;
    }

    /// <summary>
    /// Creates a <see cref="ControlLoop"/> for the given processor using the module-wide
    /// middleware chain assembled in the constructor.
    /// </summary>
    /// <remarks>
    /// If <paramref name="checkpointStore"/> implements <see cref="IFencableCheckpointStore"/>,
    /// a per-loop fence-violation handler is subscribed that cancels the loop when the lease
    /// expires for its processor. This is additive — callers (e.g. the lease group) may
    /// subscribe their own handlers independently without overwriting this one.
    /// </remarks>
    public ControlLoop Create(
        IEventProcessor processor,
        EventStoreHead head,
        IEventStoreBackend backend,
        ICheckpointStore checkpointStore,
        TimeSpan pollingInterval,
        int batchSize,
        string moduleKey,
        ProcessorExecutionOptions? executionOptions = null,
        ILogger<ControlLoop>? logger = null)
    {
        var loop = new ControlLoop(
            processor, head, backend, checkpointStore,
            pollingInterval, batchSize, moduleKey,
            _middlewares, _batchMiddlewares,
            _hasUnpairedPerEventMiddlewares,
            executionOptions,
            logger);

        // Wire a per-loop fence-violation handler through the interface rather than
        // via a concrete-type downcast. If the store is not fencable, this block is skipped
        // and fence handling is simply absent (visible to the integrator, not silent).
        if (checkpointStore is IFencableCheckpointStore fencable)
        {
            fencable.SubscribeFenceViolation(violatingProcessorId =>
            {
                // Only cancel this loop's CTS; other loops are unaffected.
                if (violatingProcessorId == loop.ProcessorId)
                    loop.Cancel();
            });
        }

        return loop;
    }
}
