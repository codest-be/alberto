using Alberto.Dcb.Configuration;
using Alberto.Dcb.Telemetry;

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
    /// </summary>
    /// <param name="retry">Retry policy (max attempts, backoff, dead-letter flag).</param>
    /// <param name="classifier">Determines whether a given exception is transient or permanent.</param>
    /// <param name="deadLetterStore">Store for exhausted events. Null disables dead-lettering.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.FailedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public static BatchConsumeMiddleware RetryAndDeadLetter(
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider = null)
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
            await deadLetterStore.StoreAsync(
                DeadLetterEntryFactory.Create(
                    context.ProcessorId,
                    envelope,
                    lastError,
                    context.Attempt,
                    clock.GetUtcNow()),
                context.CancellationToken);
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
