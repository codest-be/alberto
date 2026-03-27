# Plan 01: Batch Projection Processing

## Goal
Replace per-event `LoadMany` + `Apply` + `ApplyChanges` with a batched approach:
**1 LoadMany + N in-memory applies + 1 ApplyChanges + 1 checkpoint per batch.**

This eliminates N-1 database round-trips per batch — the single biggest performance improvement we can make.

## Current Behavior (.NET)

In `AsyncProjection<TState, TProjection>.ProcessEventAsync()`:
1. For each event: load state from store (or pending upserts)
2. Apply event in memory
3. Accumulate in `_pendingUpserts` / `_pendingDeletes`
4. On `FlushAsync()`: write all changes per tenant

The problem: step 1 does a `LoadManyAsync([docId])` for every event that isn't already in `_pendingUpserts`. For a batch of 100 events touching 50 different documents, that's 50 individual loads instead of 1.

## Reference Implementation (TS)

`packages/projections/src/polling-subscription.ts` → `processProjectionBatch()`:

```typescript
// 1. Group events by tenantId
const byTenant = new Map<string, EventEnvelope[]>();
for (const evt of events) { /* group */ }

// 2. For each tenant group:
for (const [tenantId, tenantEvents] of byTenant) {
  // 2a. Collect ALL document IDs upfront
  const docIds = new Set<string>();
  for (const evt of tenantEvents) {
    docIds.add(entry.projection.getDocumentId(evt));
  }

  // 2b. ONE loadMany for all documents
  const stateMap = await entry.stateStore.loadMany([...docIds]);

  // 2c. Apply each event in order, tracking changes in memory
  const upserts = new Map<string, T>();
  const deletes = new Set<string>();
  for (const evt of tenantEvents) {
    const docId = entry.projection.getDocumentId(evt);
    const currentState = upserts.has(docId) ? upserts.get(docId)
      : deletes.has(docId) ? undefined
      : stateMap.get(docId);
    const result = entry.projection.apply(currentState, evt);
    // accumulate into upserts/deletes
  }

  // 2d. ONE applyChanges for all documents
  await entry.stateStore.applyChanges(upserts, [...deletes]);
}

// 3. ONE checkpoint for the whole batch
await checkpointStore.save(entry.processorId, lastEvent.globalPosition);
```

On error, TS falls back to per-event processing with retry/dead-letter middleware.

## Implementation Plan

### Step 1: Add `ProcessBatchAsync` to `AsyncProjection`

Add a new method alongside the existing per-event `ProcessEventAsync`:

```csharp
public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
{
    // Group events by tenant
    var byTenant = events.GroupBy(e => e.TenantId);

    foreach (var tenantGroup in byTenant)
    {
        var tenantId = tenantGroup.Key;
        var tenantEvents = tenantGroup.ToList();

        // Get or create state store
        var entry = _stateStoreCache.AddOrUpdate(
            tenantId,
            _ => (_stateStoreFactory(tenantId), DateTimeOffset.UtcNow),
            (_, existing) => (existing.Store, DateTimeOffset.UtcNow));
        var stateStore = entry.Store;

        // 1. Collect all document IDs upfront
        var docIds = new HashSet<string>();
        foreach (var evt in tenantEvents)
            docIds.Add(_projection.GetDocumentId(evt));

        // 2. ONE loadMany for all documents in this tenant batch
        var states = await stateStore.LoadManyAsync(docIds, transaction: null, ct);

        // 3. Apply events in order, tracking changes in memory
        var upserts = new Dictionary<string, TState>();
        var deletes = new HashSet<string>();

        foreach (var evt in tenantEvents)
        {
            var docId = _projection.GetDocumentId(evt);

            // Resolve current state: pending upserts > loaded > new
            TState state;
            if (upserts.TryGetValue(docId, out var pendingState))
                state = pendingState;
            else if (deletes.Contains(docId))
                state = new TState();
            else
                state = states.GetValueOrDefault(docId) ?? new TState();

            // Idempotency check
            if (state is IProjectionEntity entity && entity.LastProcessedPosition >= evt.GlobalPosition)
                continue;

            var result = _projection.Apply(state, evt);

            switch (result)
            {
                case ProjectionResult<TState>.Set s:
                    if (s.State is IProjectionEntity projEntity)
                        projEntity.LastProcessedPosition = evt.GlobalPosition;
                    upserts[docId] = s.State;
                    deletes.Remove(docId);
                    break;
                case ProjectionResult<TState>.Delete:
                    deletes.Add(docId);
                    upserts.Remove(docId);
                    break;
            }
        }

        // 4. ONE applyChanges for all documents in this tenant
        if (upserts.Count > 0 || deletes.Count > 0)
        {
            await stateStore.ApplyChangesAsync(
                upserts, deletes.ToList(), transaction: null, ct);
        }
    }
}
```

### Step 2: Add `IBatchableProcessor` interface

```csharp
public interface IBatchableProcessor : IEventProcessor
{
    Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default);
}
```

Make `AsyncProjection` implement both `IBatchableProcessor` and `IFlushable`.

### Step 3: Update `PollingConsumer.ProcessEventsForProcessorAsync`

Change the routing logic to prefer batch processing:

```csharp
if (processor is IBatchableProcessor batchable)
{
    try
    {
        await batchable.ProcessBatchAsync(relevant, ct);
        // Checkpoint once for the whole batch
        await _checkpointStore.SaveAsync(processor.ProcessorId, relevant[^1].GlobalPosition, ct);
    }
    catch (Exception) when (/* not cancellation */)
    {
        // Fallback: per-event processing with retry/dead-letter
        foreach (var evt in relevant)
        {
            if (!processor.IsActive) break;
            await ProcessSingleEventAsync(processor, evt, ct);
        }
    }
}
else
{
    // Existing per-event path for non-batchable processors (reactors, etc.)
    await ProcessingAction();
}
```

### Step 4: Update rebuild path

`RebuildProcessorAsync` should also use batch processing when available — this is where it matters most since rebuilds process millions of events.

### Step 5: Remove `_pendingUpserts` / `_pendingDeletes` from AsyncProjection

With batch processing, the per-event accumulation is no longer needed. The batch method handles grouping internally. Keep `ProcessEventAsync` for backward compatibility (it becomes the fallback path) but simplify it to not use pending dictionaries — just load, apply, save per event.

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs` — add `ProcessBatchAsync`, implement `IBatchableProcessor`
- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — prefer batch path in `ProcessEventsForProcessorAsync` and `RebuildProcessorAsync`
- `src/Alberto.Dcb/Subscriptions/IBatchableProcessor.cs` — new interface (or add to existing file)
- `tests/Alberto.Dcb.Tests/Subscriptions/ProjectorSpecificationTests.cs` — add batch processing tests

## Risks

- EF state store concurrency: batch `applyChanges` with many entities may hit DbUpdateConcurrencyException more often. The existing retry logic in `EfStateStore` handles this.
- Memory: loading all documents for a large batch. Mitigated by batch size limits (default 100 normal, 1000 rebuild).

## Acceptance Criteria

- [ ] Batch of 100 events touching 50 documents results in 1 loadMany + 1 applyChanges (not 50+50)
- [ ] Fallback to per-event processing on batch failure
- [ ] Rebuild path uses batch processing
- [ ] Existing tests pass
- [ ] New test: batch with multiple events for same document applies them in order
- [ ] New test: batch error triggers fallback to per-event
