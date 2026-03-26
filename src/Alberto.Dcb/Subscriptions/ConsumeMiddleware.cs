using Alberto.Dcb.Telemetry;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Middleware that wraps event processing.
/// Call next() to continue the chain; don't call it to short-circuit.
/// </summary>
public delegate Task ConsumeMiddleware(ConsumeEventContext context, Func<Task> next);

/// <summary>
/// Built-in middleware factory methods.
/// </summary>
public static class ConsumeMiddlewares
{
    /// <summary>
    /// Retry with exponential backoff, dead-letter on exhaustion.
    /// </summary>
    public static ConsumeMiddleware RetryAndDeadLetter(
        ErrorPolicy? policy = null,
        IDeadLetterStore? deadLetterStore = null)
    {
        var p = policy ?? ErrorPolicy.Default;

        return async (context, next) =>
        {
            Exception? lastError = null;

            for (var attempt = 1; attempt <= p.MaxRetries + 1; attempt++)
            {
                context.Attempt = attempt;
                try
                {
                    await next();
                    context.LastError = null;
                    return; // Success
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    context.LastError = ex;

                    var classification = p.ErrorClassifier.Classify(ex);
                    if (classification == ErrorClassification.Permanent)
                        break;

                    if (attempt <= p.MaxRetries)
                    {
                        // Record retry metric
                        AlbertoMetrics.Retries.Add(1,
                            new KeyValuePair<string, object?>("processor", context.ProcessorId),
                            new KeyValuePair<string, object?>("module", context.ModuleKey));

                        await Task.Delay(p.CalculateDelay(attempt), context.CancellationToken);
                    }
                }
            }

            // Exhausted retries or permanent error — dead-letter
            context.DeadLettered = true;

            // Record dead letter metric
            AlbertoMetrics.DeadLetters.Add(1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));

            if (p.DeadLetterOnMaxRetries && deadLetterStore is not null && lastError is not null)
            {
                await deadLetterStore.StoreAsync(new DeadLetterEntry(
                    Id: Guid.NewGuid(),
                    ProcessorId: context.ProcessorId,
                    EventId: context.Envelope.Id,
                    EventType: context.Envelope.EventType.Id,
                    EventData: context.Envelope.EventData,
                    ErrorMessage: lastError.Message,
                    StackTrace: lastError.StackTrace,
                    AttemptCount: context.Attempt,
                    FailedAt: DateTimeOffset.UtcNow,
                    GlobalPosition: context.Envelope.GlobalPosition),
                    context.CancellationToken);
            }
        };
    }
}
