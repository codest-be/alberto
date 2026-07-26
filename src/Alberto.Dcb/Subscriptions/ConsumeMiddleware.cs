using Alberto.Dcb.Configuration;
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
    /// <param name="retry">Retry policy (max attempts, backoff, dead-letter flag).</param>
    /// <param name="classifier">Determines whether a given exception is transient or permanent.</param>
    /// <param name="deadLetterStore">Store for exhausted events. Null disables dead-lettering.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.FailedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public static ConsumeMiddleware RetryAndDeadLetter(
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        return async (context, next) =>
        {
            var lastError = await RetryAndDeadLetterCore.ExecuteAsync(
                context, retry, classifier, next, retryMetricCount: 1);

            if (lastError is null)
                return; // success

            // Exhausted retries or permanent error — dead-letter this event.
            context.DeadLettered = true;

            AlbertoMetrics.DeadLetters.Add(1,
                new KeyValuePair<string, object?>("processor", context.ProcessorId),
                new KeyValuePair<string, object?>("module", context.ModuleKey));

            if (retry.DeadLetterOnMaxRetries && deadLetterStore is not null)
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
                    FailedAt: clock.GetUtcNow(),
                    GlobalPosition: context.Envelope.GlobalPosition),
                    context.CancellationToken);
            }
        };
    }
}
