using Alberto.Dcb.Configuration;
using Alberto.Dcb.Telemetry;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Shared retry loop used by <see cref="ConsumeMiddlewares.RetryAndDeadLetter"/>
/// and <see cref="BatchConsumeMiddlewares.RetryAndDeadLetter"/>.
/// Centralises the attempt counter, error classification, exponential-backoff
/// delay, and retry-metric recording so that a change to retry depth or policy
/// costs one implementation instead of two.
/// </summary>
/// <remarks>
/// The method returns the last <see cref="Exception"/> on exhaustion so the
/// caller can apply its own tail behaviour — dead-letter for single events,
/// or the batch-splitting rethrow for multi-event batches.
/// </remarks>
internal static class RetryAndDeadLetterCore
{
    /// <summary>
    /// Runs the retry loop and returns <see langword="null"/> on success, or
    /// the last caught exception after all retries are exhausted (or a
    /// permanent error is encountered).
    /// </summary>
    /// <param name="context">The middleware context for the current dispatch.</param>
    /// <param name="retry">The retry knobs (max retries, delay, backoff).</param>
    /// <param name="classifier">Classifies exceptions as transient or permanent.</param>
    /// <param name="next">The inner pipeline continuation to invoke each attempt.</param>
    /// <param name="retryMetricCount">
    /// The value to add to the retry counter metric.
    /// Use <c>1</c> for single-event dispatch; use
    /// <c>context.Envelopes.Count</c> for batch dispatch so the metric
    /// reflects the actual number of events retried.
    /// </param>
    internal static async Task<Exception?> ExecuteAsync(
        IMiddlewareContext context,
        RetryOptions retry,
        IErrorClassifier classifier,
        Func<Task> next,
        int retryMetricCount)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= retry.MaxRetries + 1; attempt++)
        {
            context.Attempt = attempt;
            try
            {
                await next();
                context.LastError = null;
                return null; // success
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                context.LastError = ex;

                var classification = classifier.Classify(ex);
                if (classification == ErrorClassification.Permanent)
                    break;

                if (attempt <= retry.MaxRetries)
                {
                    AlbertoMetrics.Retries.Add(retryMetricCount,
                        ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey));

                    await Task.Delay(retry.CalculateDelay(attempt), context.CancellationToken);
                }
            }
        }

        return lastError; // exhausted or permanent — caller handles tail
    }

    /// <summary>
    /// Maximum number of times the dead-letter store write is retried on transient failure.
    /// Three attempts give two retries without a long cumulative delay.
    /// </summary>
    internal const int DeadLetterWriteMaxAttempts = 3;

    /// <summary>
    /// Writes a dead-letter entry with a bounded retry, and swallows a still-failing write
    /// after logging it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The swallow is the point. If this write throws, the exception escapes the middleware
    /// chain, the control loop marks the processor faulted, and the checkpoint stays where it
    /// was — so on the next pass every event in that window is delivered again, including the
    /// healthy ones whose side effects already committed. The event that failed is one event;
    /// the events re-delivered because of it are all of them, on every restart, with nothing in
    /// the logs saying why.
    /// </para>
    /// <para>
    /// So the trade is deliberate: losing one entry from the dead-letter queue is strictly
    /// safer than re-delivering healthy events forever, and the error log is what keeps the
    /// loss from being silent. Cancellation is the one thing that still propagates — a
    /// shutdown is not a failed write, and treating it as one would drop an entry that a
    /// restart would have stored.
    /// </para>
    /// <para>
    /// Both the single-event and batch middlewares route through here, because the reasoning
    /// above does not depend on which one is dispatching, and a fix applied to one of them is
    /// how this became a defect on the other.
    /// </para>
    /// </remarks>
    internal static async Task StoreDeadLetterAsync(
        IDeadLetterStore store,
        DeadLetterEntry entry,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        Exception? storeError = null;

        for (var attempt = 1; attempt <= DeadLetterWriteMaxAttempts; attempt++)
        {
            try
            {
                await store.StoreAsync(entry, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown — propagate cancellation so the control loop can stop cleanly.
                throw;
            }
            catch (Exception ex)
            {
                storeError = ex;
                if (attempt < DeadLetterWriteMaxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }

        logger?.LogError(
            storeError,
            "Dead-letter write failed after {Attempts} attempts for event {EventId} " +
            "on processor {ProcessorId}. The event is dropped from the dead-letter queue; " +
            "checkpoint will advance to avoid re-delivery of already-processed events.",
            DeadLetterWriteMaxAttempts,
            entry.EventId,
            entry.ProcessorId);
    }
}
