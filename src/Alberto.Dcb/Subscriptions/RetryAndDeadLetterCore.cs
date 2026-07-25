using Alberto.Dcb.Configuration;
using Alberto.Dcb.Telemetry;

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
                    AlbertoMetrics.Retries.Add(
                        retryMetricCount,
                        new KeyValuePair<string, object?>("processor", context.ProcessorId),
                        new KeyValuePair<string, object?>("module", context.ModuleKey));

                    await Task.Delay(retry.CalculateDelay(attempt), context.CancellationToken);
                }
            }
        }

        return lastError; // exhausted or permanent — caller handles tail
    }
}
