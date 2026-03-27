# Plan 08: Middleware Composition for Consume Pipeline

## Goal
Replace the current `IConsumeFilter` / `ConsumeFilterPipeline` with a composable middleware pattern: `(context, next) => Task`. This enables natural composition of cross-cutting concerns (retry, dead-letter, tracing, metrics, tenant context) without the rigidity of the current filter interface.

## Current Approach (.NET)

```csharp
public interface IConsumeFilter
{
    Task ExecuteAsync(IReadOnlyList<IEventEnvelope> events,
        ConsumeContext context, Func<Task> next, CancellationToken ct);
}
```

The filter pipeline wraps the entire batch processing. Individual concerns (retry, dead-letter, tracing) are handled differently — retry is baked into `ProcessSingleEventAsync`, dead-letter is inline, tracing is an `IConsumeFilter`.

## Reference Implementation (TS)

```typescript
type ProjectionMiddleware = (
  context: ProjectionConsumerContext,
  next: () => Promise<void>
) => Promise<void>;

// Compose-style — each middleware wraps the next
const runMiddlewares = async (ctx, middlewares, terminal) => {
  const dispatch = async (index) => {
    const middleware = middlewares[index];
    if (!middleware) { await terminal(); return; }
    await middleware(ctx, () => dispatch(index + 1));
  };
  await dispatch(0);
};

// Built-in middlewares:
// - withTracing(options) — creates spans, links to original trace
// - withMetrics(instruments) — counters + histograms
// - withRetryAndDeadLetter(options) — retry with backoff, dead-letter on exhaustion
// - withHandlerContext(wrapper) — ALS/tenant context propagation
```

Key difference from .NET: middlewares run **per-event**, not per-batch. This means retry wraps a single event processing call, which is cleaner.

## Implementation Plan

### Step 1: Define middleware delegate and context

```csharp
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Context passed through the middleware chain for each event.
/// </summary>
public sealed class ConsumeEventContext
{
    public required string ProcessorId { get; init; }
    public required string ModuleKey { get; init; }
    public required IEventEnvelope Envelope { get; init; }
    public required bool IsRebuild { get; init; }
    public int Attempt { get; set; }
    public bool DeadLettered { get; set; }
    public Exception? LastError { get; set; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Middleware that wraps event processing.
/// Call next() to continue the chain, or don't to short-circuit.
/// </summary>
public delegate Task ConsumeMiddleware(ConsumeEventContext context, Func<Task> next);
```

### Step 2: Built-in middlewares

**Retry + Dead Letter:**
```csharp
public static class ConsumeMiddlewares
{
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
                        break; // Skip retries

                    if (attempt <= p.MaxRetries)
                        await Task.Delay(p.CalculateDelay(attempt), context.CancellationToken);
                }
            }

            // Exhausted retries or permanent error
            context.DeadLettered = true;
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
                    FailedAt: DateTimeOffset.UtcNow), context.CancellationToken);
            }
        };
    }

    public static ConsumeMiddleware Tracing(/* telemetry options */)
    {
        return async (context, next) =>
        {
            // Create span, link to original trace, record attributes
            await next();
        };
    }

    public static ConsumeMiddleware Metrics(/* meters */)
    {
        return async (context, next) =>
        {
            var sw = Stopwatch.StartNew();
            await next();
            sw.Stop();
            // Record counter + histogram
        };
    }
}
```

### Step 3: Middleware runner

```csharp
internal static class MiddlewareRunner
{
    public static Task RunAsync(
        ConsumeEventContext context,
        IReadOnlyList<ConsumeMiddleware> middlewares,
        Func<Task> terminal)
    {
        return Dispatch(0);

        async Task Dispatch(int index)
        {
            if (index >= middlewares.Count)
            {
                await terminal();
                return;
            }
            await middlewares[index](context, () => Dispatch(index + 1));
        }
    }
}
```

### Step 4: Update ConsumerBuilder to accept middlewares

```csharp
public ConsumerBuilder WithMiddleware(ConsumeMiddleware middleware)
{
    _middlewares.Add(middleware);
    return this;
}

// Retry+DL middleware is added by default (can be customized or removed)
```

### Step 5: Update PollingConsumer to use middleware chain

Replace the inline retry/dead-letter logic in `ProcessSingleEventAsync` with the middleware chain. The terminal function is the actual `processor.ProcessEventAsync` call.

```csharp
private async Task ProcessSingleEventWithMiddlewareAsync(
    IEventProcessor processor, IEventEnvelope evt, CancellationToken ct)
{
    var context = new ConsumeEventContext
    {
        ProcessorId = processor.ProcessorId,
        ModuleKey = _moduleKey,
        Envelope = evt,
        IsRebuild = processor.IsRebuilding,
        CancellationToken = ct
    };

    await MiddlewareRunner.RunAsync(context, _middlewares, async () =>
    {
        await processor.ProcessEventAsync(evt, ct);
    });

    // Save checkpoint (skip if dead-lettered — already handled by middleware)
    await _checkpointStore.SaveAsync(processor.ProcessorId, evt.GlobalPosition, ct);
}
```

### Step 6: Migrate existing TelemetryConsumeFilter

Convert the existing `TelemetryConsumeFilter` (an `IConsumeFilter`) into a `ConsumeMiddleware`.

### Step 7: Deprecate IConsumeFilter

Mark `IConsumeFilter`, `IConsumeFilterPipeline`, and `ConsumeFilterPipeline` as `[Obsolete]`. They can be removed in a future version.

## Files to Create

- `src/Alberto.Dcb/Subscriptions/ConsumeEventContext.cs`
- `src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs` — delegate + built-in middlewares
- `src/Alberto.Dcb/Subscriptions/MiddlewareRunner.cs`
- `tests/Alberto.Dcb.Tests/Subscriptions/MiddlewareTests.cs`

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — use middleware chain
- `src/Alberto.Dcb/Subscriptions/Pipeline/IConsumeFilter.cs` — mark obsolete
- `src/Alberto.Dcb.Telemetry/TelemetryConsumeFilter.cs` — convert to middleware
- Consumer builder (wherever it lives) — add `WithMiddleware()`

## Acceptance Criteria

- [ ] Middleware chain executes in order (tracing → metrics → retry → terminal)
- [ ] Retry middleware respects error classification (permanent → immediate dead-letter)
- [ ] Dead letter entries created on retry exhaustion
- [ ] Tracing middleware creates spans with links to original trace
- [ ] Custom middleware can be added via builder
- [ ] Existing IConsumeFilter still works (backward compat) but is marked obsolete
- [ ] Existing tests pass
