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
    /// The retry loop is shared with <see cref="BatchConsumeMiddlewares.RetryAndDeadLetter"/>
    /// via <see cref="RetryAndDeadLetterCore"/>; this method owns only the
    /// single-event tail: marking <see cref="ConsumeEventContext.DeadLettered"/>
    /// and writing the <see cref="DeadLetterEntry"/> for the envelope.
    /// </summary>
    public static ConsumeMiddleware RetryAndDeadLetter(
        ErrorPolicy? policy = null,
        IDeadLetterStore? deadLetterStore = null)
    {
        var p = policy ?? ErrorPolicy.Default;

        return async (context, next) =>
        {
            var lastError = await RetryAndDeadLetterCore.ExecuteAsync(
                context, p, next, retryMetricCount: 1);

            if (lastError is null)
                return; // success

            // Exhausted retries or permanent error — dead-letter this event.
            context.DeadLettered = true;

            AlbertoMetrics.DeadLetters.Add(1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));

            if (p.DeadLetterOnMaxRetries && deadLetterStore is not null)
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
