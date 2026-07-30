using Alberto.Dcb.Configuration;
using Alberto.Dcb.Telemetry;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Middleware that wraps batch event processing.
/// Call next() to continue the chain; don't call it to short-circuit.
/// </summary>
public delegate Task BatchConsumeMiddleware(BatchConsumeContext context, Func<Task> next);

/// <summary>
/// Built-in middleware factory methods for batch processing.
/// </summary>
public static class BatchConsumeMiddlewares
{
    /// <summary>
    /// Maximum number of times the dead-letter store write is retried on transient failure.
    /// Three attempts give two retries without a long cumulative delay.
    /// </summary>
    private const int DeadLetterWriteMaxAttempts = 3;

    /// <summary>
    /// Retries a batch as a unit. If the batch still fails after retries,
    /// a single-event batch is dead-lettered; larger batches bubble the
    /// failure so the control loop can isolate the poison event by splitting.
    /// The retry loop is shared with <see cref="ConsumeMiddlewares.RetryAndDeadLetter"/>
    /// via <see cref="RetryAndDeadLetterCore"/>; this method owns only the
    /// batch-specific tail:
    /// <list type="bullet">
    ///   <item>Multi-event batches: rethrow via <see cref="BatchSplittingRethrow"/>
    ///   so the caller can bisect the batch to isolate the poison event.</item>
    ///   <item>Single-event batches: dead-letter in place.</item>
    /// </list>
    /// The dead-letter store write itself is wrapped in a bounded retry. If that
    /// write still cannot be made durable, the failure is logged at error level and
    /// the method returns normally — losing one dead-letter entry is strictly safer
    /// than allowing the exception to escape, which would prevent the checkpoint from
    /// advancing and cause all already-committed events to be re-delivered.
    /// </summary>
    /// <param name="retry">Retry policy (max attempts, backoff, dead-letter flag).</param>
    /// <param name="classifier">Determines whether a given exception is transient or permanent.</param>
    /// <param name="deadLetterStore">Store for exhausted events. Null disables dead-lettering.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.FailedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Logger used to surface dead-letter write failures. Null disables logging.</param>
    public static BatchConsumeMiddleware RetryAndDeadLetter(
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        return async (context, next) =>
        {
            // retryMetricCount is evaluated at dispatch time (inside the lambda)
            // so it reflects the actual batch size for the current invocation.
            var lastError = await RetryAndDeadLetterCore.ExecuteAsync(
                context, retry, classifier, next, retryMetricCount: context.Envelopes.Count);

            if (lastError is null)
                return; // success

            // Batch-splitting rethrow: bubble the failure for multi-event batches
            // so the control loop can bisect the batch to isolate the poison event.
            // For single-event batches this is a no-op and we continue to dead-letter.
            BatchSplittingRethrow(context, lastError);

            // Single-event batch — dead-letter in place.
            context.DeadLetteredCount = 1;

            AlbertoMetrics.DeadLetters.Add(1, ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey));

            if (!retry.DeadLetterOnMaxRetries || deadLetterStore is null)
                return;

            var envelope = context.Envelopes[0];
            var entry = DeadLetterEntryFactory.Create(
                context.ProcessorId,
                envelope,
                lastError,
                context.Attempt,
                clock.GetUtcNow());

            // Bounded retry for the dead-letter write: a transient DB error here must NOT
            // escape. If it does, the control loop marks IsFaulted and the checkpoint stays
            // at the pre-batch position, so every event that already committed side-effects
            // (A, B, C in a bisected [A,B,C,D]) is re-delivered on restart — a silent
            // double-delivery with no signal.
            // Trade-off: losing one entry from the dead-letter queue is strictly safer than
            // infinitely re-delivering healthy events that already committed.
            Exception? storeError = null;
            for (var attempt = 1; attempt <= DeadLetterWriteMaxAttempts; attempt++)
            {
                try
                {
                    await deadLetterStore.StoreAsync(entry, context.CancellationToken);
                    storeError = null;
                    break;
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    // Shutdown — propagate cancellation so the control loop can stop cleanly.
                    throw;
                }
                catch (Exception ex)
                {
                    storeError = ex;
                    if (attempt < DeadLetterWriteMaxAttempts)
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(100 * attempt),
                            context.CancellationToken);
                }
            }

            if (storeError is not null)
            {
                // Dead-letter write failed after all retries. Log loudly so the operator
                // knows event {EventId} was dropped from the dead-letter queue, but do NOT
                // rethrow: the checkpoint must advance to avoid re-delivering the events
                // that already committed (see comment on the retry loop above).
                logger?.LogError(
                    storeError,
                    "Dead-letter write failed after {Attempts} attempts for event {EventId} " +
                    "on processor {ProcessorId}. The event is dropped from the dead-letter queue; " +
                    "checkpoint will advance to avoid re-delivery of already-processed events.",
                    DeadLetterWriteMaxAttempts,
                    envelope.Id,
                    context.ProcessorId);
            }
        };
    }

    /// <summary>
    /// Rethrows the batch failure when the batch contains more than one event,
    /// so the caller can bisect the batch to isolate the poison event.
    /// Does nothing for single-event batches (they are dead-lettered in-place).
    /// </summary>
    private static void BatchSplittingRethrow(BatchConsumeContext context, Exception? lastError)
    {
        if (context.Envelopes.Count > 1)
            throw lastError ?? new InvalidOperationException("Batch processing failed without an exception.");
    }
}
