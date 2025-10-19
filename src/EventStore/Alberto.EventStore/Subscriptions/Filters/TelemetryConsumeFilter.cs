using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.Batching;
using Alberto.EventStore.Subscriptions.Subscriptions;

namespace Alberto.EventStore.Subscriptions.Filters;

/// <summary>
/// Filter that creates telemetry trace context from event metadata to enable end-to-end tracing
/// </summary>
public sealed class TelemetryConsumeFilter(
    ITraceContextProvider traceContextProvider,
    bool isSynchronous) : IConsumeFilter
{
    public async ValueTask Execute(
        object @event,
        EventContext context,
        Func<ValueTask> next,
        CancellationToken cancellationToken = default)
    {
        // Skip individual event traces when in batch mode - the batch will create its own trace with links
        var batchScope = ProjectionBatchScope.Current;
        if (batchScope != null)
        {
            // No trace scope for individual events when batching
            await next();
            return;
        }

        // Create telemetry scope from event metadata (may be no-op if no telemetry configured)
        using var traceScope =
            traceContextProvider.CreateScopeFromMetadata(context.Metadata, context.SubscriptionName, context.EventType, isSynchronous);

        // Continue with the pipeline within the trace context
        await next();
    }
}