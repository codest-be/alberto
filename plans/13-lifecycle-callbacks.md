# Plan 13: `OnProjected` / `OnRebuildComplete` Callbacks

## Goal
Add lifecycle callbacks to the polling consumer for observability:
- `OnProjected(processorId, envelope)` — called after each event is successfully processed
- `OnRebuildComplete(processorId)` — called when a rebuild finishes

These are essential for integration testing (waiting for projections to catch up) and for audit/logging purposes.

## Reference Implementation (TS)

`packages/projections/src/polling-subscription.ts`:

```typescript
interface PollingSubscriptionOptions {
  /**
   * Called after each event is successfully processed by a projection or raw handler.
   * Use in tests to observe projection completions without polling.
   * MUST NOT be set in production.
   */
  onProjected?: (processorId: string, envelope: EventEnvelope) => void;

  /**
   * Called when a rebuild finishes (whether by catching up or by error/abort).
   * Use to emit audit-trail entries for rebuild completion.
   */
  onRebuildComplete?: (processorId: string) => void;
}
```

Used in polling loop:
```typescript
// After successful event processing:
onProjected?.(entry.processorId, evt);

// In rebuildProcessor finally block:
onRebuildComplete?.(entry.processorId);
```

TS also uses `onProjected` in `@alberto/testing`:
```typescript
// EventCollector subscribes to onProjected and stores projected events
// waitForProjected polls the collector until a predicate matches
class EventCollector {
  onProjected(processorId, envelope) {
    this.projected.push({ processorId, envelope });
    // Wake up any waiters
  }

  async waitForProjected(predicate, timeoutMs = 5000) {
    // Poll until predicate matches or timeout
  }
}
```

## Implementation Plan

### Step 1: Add callback properties to PollingConsumer

```csharp
// In PollingConsumer constructor or via a setter:

/// <summary>
/// Called after each event is successfully processed by a processor.
/// Use for test observability. Do not perform heavy work in this callback.
/// </summary>
public Action<string, IEventEnvelope>? OnProjected { get; set; }

/// <summary>
/// Called when a processor completes a rebuild (caught up within threshold).
/// Use for audit logging or notification.
/// </summary>
public Action<string>? OnRebuildComplete { get; set; }
```

### Step 2: Invoke callbacks in processing paths

**In `ProcessSingleEventAsync` (after successful processing):**
```csharp
// After checkpoint save on success:
await _checkpointStore.SaveAsync(processor.ProcessorId, evt.GlobalPosition, ct);
OnProjected?.Invoke(processor.ProcessorId, evt);
return;
```

**In batch processing path (plan 01) — after each event:**
```csharp
// After applying each event in the batch:
OnProjected?.Invoke(ProcessorId, evt);
```

**In `RebuildProcessorAsync` finally block:**
```csharp
finally
{
    processor.IsRebuilding = false;
    lock (_rebuildTasksLock)
    {
        _rebuildTasks.Remove(processor.ProcessorId);
    }
    OnRebuildComplete?.Invoke(processor.ProcessorId);  // NEW
}
```

### Step 3: Add to ConsumerBuilder

```csharp
public ConsumerBuilder OnProjected(Action<string, IEventEnvelope> callback)
{
    _onProjected = callback;
    return this;
}

public ConsumerBuilder OnRebuildComplete(Action<string> callback)
{
    _onRebuildComplete = callback;
    return this;
}
```

### Step 4: EventCollector test helper

Create a test helper in the test project (or a new `Alberto.Dcb.Testing` package):

```csharp
namespace Alberto.Dcb.Testing;

/// <summary>
/// Test helper that collects projected events and supports waiting for specific projections.
/// Wire to PollingConsumer.OnProjected to use.
/// </summary>
public sealed class EventCollector
{
    private readonly List<(string ProcessorId, IEventEnvelope Envelope)> _projected = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>
    /// Call this from OnProjected callback.
    /// </summary>
    public void OnProjected(string processorId, IEventEnvelope envelope)
    {
        lock (_projected)
        {
            _projected.Add((processorId, envelope));
        }
        _signal.Release();
    }

    /// <summary>
    /// Wait until a projected event matches the predicate, or timeout.
    /// </summary>
    public async Task<IEventEnvelope> WaitForProjectedAsync(
        Func<string, IEventEnvelope, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_projected)
            {
                var match = _projected.FirstOrDefault(p => predicate(p.ProcessorId, p.Envelope));
                if (match.Envelope is not null) return match.Envelope;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            await _signal.WaitAsync(remaining, ct);
        }

        throw new TimeoutException("Timed out waiting for projected event.");
    }

    /// <summary>
    /// Wait until a specific processor has processed an event of a given type.
    /// </summary>
    public Task<IEventEnvelope> WaitForProjectedAsync(
        string processorId, string eventType,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => WaitForProjectedAsync(
            (pid, env) => pid == processorId && env.EventType.Id == eventType,
            timeout, ct);
}
```

### Step 5: Usage in tests

```csharp
var collector = new EventCollector();
consumer.OnProjected = collector.OnProjected;

// Append an event
await eventStore.AppendAsync(tenantId, [new OrderCreatedEvent(...)]);

// Wait for projection to process it
var envelope = await collector.WaitForProjectedAsync("order-summary-v1", "order-created");

// Assert projection state
var state = await stateStore.LoadManyAsync(["order-123"]);
Assert.NotNull(state["order-123"]);
```

## Files to Create

- `tests/Alberto.Dcb.Tests/Testing/EventCollector.cs` (or `src/Alberto.Dcb.Testing/EventCollector.cs` if creating a testing package)

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — add properties, invoke callbacks
- Consumer builder — add `OnProjected()` and `OnRebuildComplete()` methods
- `src/Alberto.Dcb/Subscriptions/IEventConsumer.cs` — optionally add callback properties to interface

## Acceptance Criteria

- [ ] `OnProjected` called after each successful event processing
- [ ] `OnRebuildComplete` called when rebuild finishes
- [ ] Callbacks are null by default (no overhead when not set)
- [ ] `EventCollector.WaitForProjectedAsync` works in integration tests
- [ ] Callbacks fire in both normal and rebuild processing paths
- [ ] Callbacks fire in both per-event and batch processing paths (plan 01)
- [ ] No callback invocation for dead-lettered events
