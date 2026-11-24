using Alberto.Configuration;
using Alberto.Telemetry;
using Microsoft.Extensions.Logging;
using Alberto.DeadLetters;

namespace Alberto.Subscriptions;

/// <summary>
/// Shared retry loop used by <see cref="ConsumeMiddlewares.RetryAndDeadLetter"/>
/// and <see cref="BatchConsumeMiddlewares.RetryAndDeadLetter"/>.
/// Centralises the attempt counter, error classification, exponential-backoff
/// delay, and retry-metric recording so that a change to retry depth or policy
/// costs one implementation instead of two.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> returns the last <see cref="Exception"/> on exhaustion;
/// <see cref="ExecuteAndDeadLetterAsync"/> adds the halt / dead-letter tail both middlewares share.
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
    /// <param name="instruments">Where the retry counter is recorded.</param>
    /// <param name="clock">Clock the backoff waits on. Defaults to <see cref="TimeProvider.System"/>.</param>
    internal static async Task<Exception?> ExecuteAsync(
        IMiddlewareContext context,
        RetryOptions retry,
        IErrorClassifier classifier,
        Func<Task> next,
        int retryMetricCount,
        AlbertoInstruments instruments,
        TimeProvider? clock = null)
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
                    instruments.Retries.Add(retryMetricCount,
                        ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey));

                    await Task.Delay(retry.CalculateDelay(attempt), clock ?? TimeProvider.System, context.CancellationToken);
                }
            }
        }

        return lastError; // exhausted or permanent — caller handles tail
    }

    /// <summary>
    /// The whole failure path of a retrying middleware: the retry loop, then, on failure, either
    /// the halt (a projection stays on its event) or the dead letter. Returns <see langword="null"/>
    /// when the dispatch succeeded or the processor halted and then recovered, and otherwise
    /// whether the dead-letter entry was stored (false also when none was written).
    /// </summary>
    /// <param name="context">The middleware context for the current dispatch.</param>
    /// <param name="envelope">The event a halt reports and a dead letter records: the only one, or a batch's first.</param>
    /// <param name="priorRedeliveries">Redeliveries the event's dead-letter row already had; null for a batch.</param>
    /// <param name="rethrowFailure">
    /// Rethrow the failure instead of halting or dead-lettering, so the control loop can bisect a
    /// multi-event batch to isolate the poison event.
    /// </param>
    /// <param name="retryMetricCount">See <see cref="ExecuteAsync"/>.</param>
    /// <param name="haltObserver">Told when the processor halts and resumes.</param>
    /// <param name="policy">The processor's policy; null for custom wiring, which dead-letters.</param>
    /// <param name="effectiveRetry">The retry options in force: the policy's, else the middleware's.</param>
    /// <param name="classifier">Determines whether a given exception is transient or permanent.</param>
    /// <param name="deadLetterStore">Store for exhausted events. Null disables dead-lettering.</param>
    /// <param name="next">The rest of the chain.</param>
    /// <param name="instruments">The container's instruments.</param>
    /// <param name="clock">Clock for backoff, <c>FailedAt</c> and the redelivery plan.</param>
    /// <param name="logger">Logger used to surface dead-letter write failures. Null disables logging.</param>
    internal static async Task<bool?> ExecuteAndDeadLetterAsync(
        IMiddlewareContext context,
        IEventEnvelope envelope,
        int? priorRedeliveries,
        bool rethrowFailure,
        int retryMetricCount,
        IHaltObserver? haltObserver,
        ProcessorPolicy? policy,
        RetryOptions effectiveRetry,
        IErrorClassifier classifier,
        IDeadLetterStore? deadLetterStore,
        Func<Task> next,
        AlbertoInstruments instruments,
        TimeProvider clock,
        ILogger? logger)
    {
        var lastError = await ExecuteAsync(context, effectiveRetry, classifier, next, retryMetricCount, instruments, clock);
        if (lastError is null)
            return null; // success

        if (rethrowFailure)
            throw lastError;

        // A projection never dead-letters or skips: it stays on this event until it works.
        if (policy is { HaltsOnFailure: true })
        {
            await HaltAsync(context, haltObserver, envelope.GlobalPosition, lastError, policy.Halt, next, clock, logger, instruments);
            return null;
        }

        // Exhausted retries or permanent error — dead-letter this event.
        instruments.DeadLetters.Add(1, ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey));

        if (!effectiveRetry.DeadLetterOnMaxRetries || deadLetterStore is null)
            return false;

        var now = clock.GetUtcNow();
        var (nextRetryAt, redeliveryCount) = PlanRedelivery(policy, classifier, lastError, priorRedeliveries, now);
        var entry = DeadLetterEntryFactory.Create(
            context.ProcessorId, envelope, lastError, context.Attempt, now, nextRetryAt, redeliveryCount);

        return await StoreDeadLetterAsync(deadLetterStore, entry, logger, context.CancellationToken, clock);
    }

    /// <summary>
    /// When a freshly dead-lettered event should come back, and how many redeliveries it has had.
    /// The wait is null (parked for manual retry) when redelivery is off, the budget is spent, or
    /// the error is not one the policy redelivers.
    /// </summary>
    /// <param name="policy">The processor's policy. Null (legacy wiring) never redelivers.</param>
    /// <param name="classifier">Judges whether the error is transient, permanent or unknown.</param>
    /// <param name="error">The failure being dead-lettered.</param>
    /// <param name="priorRedeliveries">Redeliveries the row had before this failure; null on the first dead-lettering.</param>
    /// <param name="now">The application clock.</param>
    internal static (DateTimeOffset? NextRetryAt, int RedeliveryCount) PlanRedelivery(
        ProcessorPolicy? policy,
        IErrorClassifier classifier,
        Exception error,
        int? priorRedeliveries,
        DateTimeOffset now)
    {
        var count = priorRedeliveries is { } prior ? prior + 1 : 0;

        if (policy?.Redelivery is not { Enabled: true } redelivery || count >= redelivery.MaxRedeliveries)
            return (null, count);

        var classification = classifier.Classify(error);
        var redeliverable = classification == ErrorClassification.Transient
            || (classification == ErrorClassification.Unknown
                && redelivery.RedeliverWhen == RedeliverWhen.TransientOrUnknown);

        return redeliverable
            ? (now + redelivery.CalculateDelay(count + 1), count)
            : (null, count);
    }

    /// <summary>
    /// The projection tail: never dead-letter, never move on. Retries the failing event in
    /// place on the halt backoff until it succeeds, reporting the processor as halted meanwhile.
    /// Only cancellation ends it early.
    /// </summary>
    /// <remarks>
    /// The log names the processor and the global position, never the event data. The loop
    /// waits on the injected clock, so a test advances it instead of sleeping.
    /// </remarks>
    internal static async Task HaltAsync(
        IMiddlewareContext context,
        IHaltObserver? observer,
        long globalPosition,
        Exception firstError,
        HaltOptions halt,
        Func<Task> next,
        TimeProvider clock,
        ILogger? logger,
        AlbertoInstruments instruments)
    {
        var tags = ProcessorTags.ForModule(context.ProcessorId, context.ModuleKey);

        instruments.ProcessorHalts.Add(1, tags);
        instruments.ProcessorsHalted.Add(1, tags);
        observer?.Halted(globalPosition);

        logger?.LogError(
            firstError,
            "Processor {ProcessorId} halted at event position {GlobalPosition}: the event cannot be " +
            "processed, so the processor stays on it and retries in place without dead-lettering or " +
            "skipping it. Later events wait until it succeeds.",
            context.ProcessorId,
            globalPosition);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await Task.Delay(halt.CalculateDelay(attempt), clock, context.CancellationToken);

                context.Attempt++;
                try
                {
                    await next();
                    context.LastError = null;
                    logger?.LogInformation(
                        "Processor {ProcessorId} resumed after {Attempts} in-place retries at event position {GlobalPosition}.",
                        context.ProcessorId,
                        attempt,
                        globalPosition);
                    return;
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    context.LastError = ex;
                    logger?.LogDebug(
                        ex,
                        "Processor {ProcessorId} is still halted at event position {GlobalPosition} (in-place retry {Attempt}).",
                        context.ProcessorId,
                        globalPosition,
                        attempt);
                }
            }
        }
        finally
        {
            instruments.ProcessorsHalted.Add(-1, tags);
            observer?.Resumed();
        }
    }

    /// <summary>
    /// Maximum number of times the dead-letter store write is retried on transient failure.
    /// Three attempts give two retries without a long cumulative delay.
    /// </summary>
    internal const int DeadLetterWriteMaxAttempts = 3;

    /// <summary>
    /// Writes a dead-letter entry with a bounded retry, and swallows a still-failing write
    /// after logging it. Returns whether the entry was stored.
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
    /// The single-event and batch middlewares both reach here through
    /// <see cref="ExecuteAndDeadLetterAsync"/>, because the reasoning above does not depend on
    /// which one is dispatching, and a fix applied to one of them is how this became a defect
    /// on the other.
    /// </para>
    /// </remarks>
    internal static async Task<bool> StoreDeadLetterAsync(
        IDeadLetterStore store,
        DeadLetterEntry entry,
        ILogger? logger,
        CancellationToken cancellationToken,
        TimeProvider? clock = null)
    {
        Exception? storeError = null;

        for (var attempt = 1; attempt <= DeadLetterWriteMaxAttempts; attempt++)
        {
            try
            {
                await store.StoreAsync(entry, cancellationToken);
                return true;
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
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), clock ?? TimeProvider.System, cancellationToken);
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
        return false;
    }
}
