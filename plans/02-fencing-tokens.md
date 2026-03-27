# Plan 02: Fencing Tokens on Checkpoint Writes

## Goal
Prevent zombie consumers from writing stale checkpoints after their lease expires. When a consumer's lease is taken over by another replica, any in-flight checkpoint writes from the old consumer must be rejected.

## The Problem

Current flow:
1. Replica A holds lease for tenant X
2. Replica A processes event at position 500, prepares to save checkpoint
3. Replica A's lease expires (slow GC pause, network delay, etc.)
4. Replica B acquires lease for tenant X, starts processing from checkpoint (say position 450)
5. Replica B processes up to position 480, saves checkpoint = 480
6. Replica A's checkpoint save finally executes: checkpoint = 500
7. Replica B now skips events 481-500 because checkpoint says 500

This is the "zombie fencing" problem. TS solves it with `writeCheckpointIfLeaseHeld`.

## Reference Implementation (TS)

`packages/projections/src/coordinator/lease-store.ts`:

```typescript
async writeCheckpointIfLeaseHeld(processorId, nodeId, position) {
  const result = await sql`
    UPDATE processor_checkpoints
    SET last_position = ${position}, updated_at = now()
    WHERE processor_id = ${processorId}
    AND EXISTS (
      SELECT 1 FROM processor_leases
      WHERE processor_id = ${processorId}
      AND node_id = ${nodeId}
      AND expires_at > now()
    )
  `;
  return result.count > 0;
}
```

The checkpoint write only succeeds if the lease is still held by the writing node.

## Implementation Plan

### Step 1: Add fenced checkpoint write to PostgreSQL

New SQL function (add as migration 012):

```sql
CREATE OR REPLACE FUNCTION {schema}.save_checkpoint_if_lease_held(
    p_processor_id TEXT,
    p_consumer_id TEXT,
    p_replica_id TEXT,
    p_position BIGINT
) RETURNS BOOLEAN AS $$
DECLARE
    v_updated BOOLEAN;
BEGIN
    UPDATE {schema}.processor_checkpoints
    SET last_position = p_position, updated_at = now()
    WHERE processor_id = p_processor_id
    AND EXISTS (
        SELECT 1 FROM {schema}.tenant_leases
        WHERE consumer_id = p_consumer_id
        AND replica_id = p_replica_id
        AND expires_at > now()
    );

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated > 0;
END;
$$ LANGUAGE plpgsql;
```

### Step 2: Add `IFencedCheckpointStore` interface

```csharp
public interface IFencedCheckpointStore : ICheckpointStore
{
    /// <summary>
    /// Saves checkpoint only if the specified replica still holds an active lease.
    /// Returns false if the lease has expired (fenced off).
    /// </summary>
    Task<bool> SaveIfLeaseHeldAsync(
        string processorId, long position,
        string consumerId, string replicaId,
        CancellationToken ct = default);
}
```

### Step 3: Implement in `PostgresCheckpointStore`

Add the fenced write method that calls the new SQL function.

### Step 4: Update `CachingCheckpointStore` to support fencing

The caching layer needs to be aware of fencing. Options:
- **Option A**: Pass consumer/replica context through the cache. The cache's flush timer calls `SaveIfLeaseHeldAsync` instead of `SaveAsync` when fencing info is available.
- **Option B**: Store fencing context per processor ID in the cache.

Recommend Option A: add an optional `FencingContext` (consumerId + replicaId) that the `PollingConsumer` sets when configuring the cache.

### Step 5: Update `PollingConsumer` to use fenced writes

In tenant-distributed mode, checkpoint writes should use `SaveIfLeaseHeldAsync`. In single-leader mode, the existing `SaveAsync` is fine (pg advisory lock provides the fencing).

The consumer already knows its `ConsumerId` and `_replicaId`. Pass these through to the checkpoint store.

If a fenced write returns false, the consumer should:
1. Log a warning
2. Remove the tenant from `_ownedTenants`
3. Remove the tenant lease from `_tenantLeases`
4. Not process further events for that tenant

### Step 6: Handle fenced writes in `CachingCheckpointStore` flush

When the flush timer fires and a fenced write fails, the dirty entry should be removed (not retried) and the consumer should be notified. Use an event or callback pattern.

## Files to Create/Modify

- `src/Alberto.Dcb.Postgres/Migrations/012_fenced_checkpoint.sql` — new migration
- `src/Alberto.Dcb/Subscriptions/IFencedCheckpointStore.cs` — new interface
- `src/Alberto.Dcb.Postgres/PostgresCheckpointStore.cs` — implement fenced write
- `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs` — support fencing context
- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — use fenced writes in tenant-distributed mode
- `tests/Alberto.Dcb.Tests/Subscriptions/FencedCheckpointTests.cs` — new tests

## Acceptance Criteria

- [ ] Checkpoint write succeeds when lease is held
- [ ] Checkpoint write returns false (no-op) when lease expired
- [ ] Consumer stops processing tenant events on fenced write failure
- [ ] CachingCheckpointStore properly handles fenced writes during flush
- [ ] Single-leader mode continues using unfenced writes (no regression)
- [ ] Integration test: simulate lease expiry → verify checkpoint write rejected
