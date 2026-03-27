# Plan 10: Dead Letter Retry by Checkpoint Rewind

## Goal
Change the dead letter retry approach from "re-process single event out of order" to "rewind checkpoint to just before the first dead-lettered event and re-process in order." This is semantically cleaner because events are always processed in sequence.

## Current Approach (.NET)

`AdminQueryService.RetryDeadLetterAsync`:
1. Load the dead letter entry
2. Find the original event in the event store
3. Call `processor.ProcessEventAsync(event)` directly
4. If success, delete the dead letter entry

Problems:
- Event is processed out of order (state may have changed since it was dead-lettered)
- Uses reflection to access `_processors` list
- Only retries a single event — what if there are 50 dead letters in sequence?

## Reference Approach (TS)

`packages/operations/src/operations.ts`:

```typescript
// Dead letter retry:
// 1. Find the earliest dead-lettered event position for this processor
// 2. Delete all dead letter entries for the processor
// 3. Rewind the checkpoint to just before that position
// → The polling consumer naturally re-processes from there

async retry(processorId: string, opts?: { fromPosition?: bigint }) {
    const entries = await getDeadLetters(processorId);
    if (entries.length === 0) return;

    const minPosition = entries.reduce(
        (min, e) => e.globalPosition < min ? e.globalPosition : min,
        entries[0].globalPosition
    );

    // Delete DL entries
    await sql`DELETE FROM dead_letter_entries WHERE processor_id = ${processorId}`;

    // Rewind checkpoint to just before the first dead letter
    const rewindTo = (opts?.fromPosition ?? minPosition) - 1n;
    await sql`UPDATE processor_checkpoints SET last_position = ${rewindTo} WHERE processor_id = ${processorId}`;
}
```

The consumer's normal poll loop then picks up from the rewound position, re-processing the previously dead-lettered events along with any events that came after.

## Implementation Plan

### Step 1: Add `RetryByRewindAsync` to `IAdminQueryService`

```csharp
/// <summary>
/// Retries dead letters for a processor by rewinding its checkpoint
/// to just before the earliest dead-lettered event.
/// The consumer will naturally re-process from there.
/// </summary>
/// <param name="processorId">The processor to retry.</param>
/// <param name="fromPosition">Optional: rewind to this specific position instead of auto-detecting.</param>
Task<DeadLetterRewindResult> RetryByRewindAsync(
    string processorId,
    long? fromPosition = null,
    CancellationToken ct = default);

public record DeadLetterRewindResult(
    string ProcessorId,
    int DeadLetterCount,
    long RewindPosition,
    long PreviousPosition);
```

### Step 2: Implement in `AdminQueryService`

```csharp
public async Task<DeadLetterRewindResult> RetryByRewindAsync(
    string processorId, long? fromPosition, CancellationToken ct)
{
    // 1. Get dead letters for this processor
    var deadLetters = await _dataAccess.ListDeadLettersAsync(processorId, ...);
    if (deadLetters.Items.Count == 0)
        throw new InvalidOperationException($"No dead letters for processor '{processorId}'.");

    // 2. Find the earliest dead-lettered event position
    // Need to look up original events to get their positions
    var earliestPosition = long.MaxValue;
    foreach (var dl in deadLetters.Items)
    {
        var evt = await _dataAccess.GetEventByIdAsync(dl.EventId, ct);
        if (evt is not null && evt.GlobalPosition < earliestPosition)
            earliestPosition = evt.GlobalPosition;
    }

    // Or if fromPosition is specified, use that
    var rewindTo = (fromPosition ?? earliestPosition) - 1;
    rewindTo = Math.Max(0, rewindTo);

    // 3. Get current checkpoint for return value
    var currentCheckpoint = await _checkpointStore.GetAsync(processorId, ct) ?? 0;

    // 4. Clear dead letter entries for this processor
    await _deadLetterStore!.ClearAsync(processorId, ct);

    // 5. Rewind checkpoint
    await _checkpointStore.SaveAsync(processorId, rewindTo, ct);

    return new DeadLetterRewindResult(
        ProcessorId: processorId,
        DeadLetterCount: deadLetters.Items.Count,
        RewindPosition: rewindTo,
        PreviousPosition: currentCheckpoint);
}
```

### Step 3: Add REST endpoint

```csharp
// In DeadLettersEndpoints.cs:
group.MapPost("/{processorId}/retry-rewind", async (
    string processorId,
    long? fromPosition,
    IAdminQueryService service,
    CancellationToken ct) =>
{
    var result = await service.RetryByRewindAsync(processorId, fromPosition, ct);
    return Results.Ok(result);
});
```

### Step 4: Add position to dead letter entries

The current `DeadLetterEntry` doesn't store the global position of the original event. Add it so we don't need to look up events by ID:

```csharp
public record DeadLetterEntry(
    Guid Id,
    string ProcessorId,
    Guid EventId,
    string EventType,
    string EventData,
    string ErrorMessage,
    string? StackTrace,
    int AttemptCount,
    DateTimeOffset FailedAt,
    long GlobalPosition = 0);  // NEW: position of the original event
```

This requires updating `PollingConsumer.DeadLetterEventAsync` to pass `evt.GlobalPosition` and a migration to add the column.

### Step 5: Keep existing single-event retry (backward compat)

Keep `RetryDeadLetterAsync` for cases where you want to retry just one specific dead letter. But make the rewind approach the recommended default for bulk retry.

### Step 6: Add audit logging

Log the rewind operation to the audit trail (plan 09).

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/DeadLetterEntry.cs` — add `GlobalPosition` field
- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — pass `evt.GlobalPosition` to dead letter
- `src/Alberto.Dcb.Admin/Internal/IAdminQueryService.cs` — add `RetryByRewindAsync`
- `src/Alberto.Dcb.Admin/Internal/AdminQueryService.cs` — implement rewind
- `src/Alberto.Dcb.Admin/Endpoints/DeadLettersEndpoints.cs` — add endpoint
- `src/Alberto.Dcb.Admin/Api/Models/` — add `DeadLetterRewindResult`

## Files to Create (migration)

- `src/Alberto.Dcb.Postgres/Migrations/015_dead_letter_position.sql` — add `global_position` column

## Acceptance Criteria

- [ ] `RetryByRewindAsync` clears dead letters and rewinds checkpoint
- [ ] Consumer naturally re-processes from the rewound position
- [ ] Events are re-processed in order (not out of order like single-event retry)
- [ ] Dead letter entry includes global position of original event
- [ ] REST endpoint exposed for rewind retry
- [ ] Works with specific `fromPosition` override
- [ ] Existing single-event retry still works
