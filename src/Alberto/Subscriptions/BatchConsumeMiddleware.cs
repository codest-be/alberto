using Alberto.Configuration;
using Alberto.Telemetry;
using Microsoft.Extensions.Logging;
using Alberto.DeadLetters;

namespace Alberto.Subscriptions;

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
    /// The retry loop and the halt / dead-letter tail are shared with
    /// <see cref="ConsumeMiddlewares.RetryAndDeadLetter"/> via <see cref="RetryAndDeadLetterCore"/>;
    /// this method owns only the batch-specific part: a multi-event batch rethrows so the caller
    /// can bisect it to isolate the poison event, and a single-event batch is dead-lettered in place.
    /// A dead-letter write that still fails is logged, not thrown; see
    /// <see cref="RetryAndDeadLetterCore.StoreDeadLetterAsync"/> for why.
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
        => RetryAndDeadLetterOn(AlbertoMetrics.Shared, retry, classifier, deadLetterStore, timeProvider, logger);

    /// <summary>As the public overload, recording on the container's instruments.</summary>
    internal static BatchConsumeMiddleware RetryAndDeadLetterOn(
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

            // retryMetricCount is evaluated at dispatch time (inside the lambda)
            // so it reflects the actual batch size for the current invocation.
            // A multi-event batch rethrows so the control loop can bisect it; only a
            // single-event batch is halted on or dead-lettered, in place.
            if (await RetryAndDeadLetterCore.ExecuteAndDeadLetterAsync(
                    context, context.Envelopes[0], priorRedeliveries: null,
                    rethrowFailure: context.Envelopes.Count > 1, retryMetricCount: context.Envelopes.Count,
                    context.HaltObserver, policy, effectiveRetry, classifier, deadLetterStore, next,
                    instruments, clock, logger) is not null)
                context.DeadLetteredCount = 1;
        };
    }
}
