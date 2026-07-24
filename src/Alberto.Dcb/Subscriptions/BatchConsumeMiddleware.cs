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
    /// </summary>
    public static BatchConsumeMiddleware RetryAndDeadLetter(
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore)
    {
        return async (context, next) =>
        {
            Exception? lastError = null;

            for (var attempt = 1; attempt <= retry.MaxRetries + 1; attempt++)
            {
                context.Attempt = attempt;
                try
                {
                    await next();
                    context.LastError = null;
                    return;
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
                            context.Envelopes.Count,
                            new KeyValuePair<string, object?>("processor", context.ProcessorId),
                            new KeyValuePair<string, object?>("module", context.ModuleKey));

                        await Task.Delay(retry.CalculateDelay(attempt), context.CancellationToken);
                    }
                }
            }

            if (context.Envelopes.Count > 1)
                throw lastError ?? new InvalidOperationException("Batch processing failed without an exception.");

            context.DeadLetteredCount = 1;

            AlbertoMetrics.DeadLetters.Add(
                1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));

            if (!retry.DeadLetterOnMaxRetries || deadLetterStore is null || lastError is null)
                return;

            var envelope = context.Envelopes[0];
            await deadLetterStore.StoreAsync(
                new DeadLetterEntry(
                    Id: Guid.NewGuid(),
                    ProcessorId: context.ProcessorId,
                    EventId: envelope.Id,
                    EventType: envelope.EventType.Id,
                    EventData: envelope.EventData,
                    ErrorMessage: lastError.Message,
                    StackTrace: lastError.StackTrace,
                    AttemptCount: context.Attempt,
                    FailedAt: DateTimeOffset.UtcNow,
                    GlobalPosition: envelope.GlobalPosition),
                context.CancellationToken);
        };
    }
}
