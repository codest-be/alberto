using Alberto.Configuration;
using Alberto.Telemetry;
using Microsoft.Extensions.Logging;
using Alberto.DeadLetters;

namespace Alberto.Subscriptions;

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
    /// via <see cref="RetryAndDeadLetterCore"/>, as is the halt / dead-letter tail; this method
    /// owns only the single-event part: counting redeliveries and marking
    /// <see cref="ConsumeEventContext.DeadLettered"/>.
    /// </summary>
    /// <param name="retry">Retry policy (max attempts, backoff, dead-letter flag).</param>
    /// <param name="classifier">Determines whether a given exception is transient or permanent.</param>
    /// <param name="deadLetterStore">Store for exhausted events. Null disables dead-lettering.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="DeadLetterEntry.FailedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Logger used to surface dead-letter write failures. Null disables logging.</param>
    public static ConsumeMiddleware RetryAndDeadLetter(
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        => RetryAndDeadLetterOn(AlbertoMetrics.Shared, retry, classifier, deadLetterStore, timeProvider, logger);

    /// <summary>As the public overload, recording on the container's instruments.</summary>
    internal static ConsumeMiddleware RetryAndDeadLetterOn(
        AlbertoInstruments instruments,
        RetryOptions retry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        TimeProvider? timeProvider,
        ILogger? logger,
        ProcessorPolicies? policies = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        return async (context, next) =>
        {
            var policy = policies?.For(context.ProcessorId);
            var effectiveRetry = policy?.Retry ?? retry;

            if (context.PriorRedeliveries is not null)
                instruments.Redeliveries.Add(1, ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey));

            if (await RetryAndDeadLetterCore.ExecuteAndDeadLetterAsync(
                    context, context.Envelope, context.PriorRedeliveries, rethrowFailure: false, retryMetricCount: 1,
                    context.HaltObserver, policy, effectiveRetry, classifier, deadLetterStore, next,
                    instruments, clock, logger) is { } stored)
            {
                context.DeadLettered = true;
                context.DeadLetterStored = stored;
            }
        };
    }
}
