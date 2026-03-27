# Plan 11: Consistent Hash Ring for Tenant Assignment

## Goal
Replace the current random/first-come tenant acquisition with a consistent hash ring that deterministically assigns tenants to nodes. This provides predictable distribution and minimizes tenant movement when nodes join/leave.

## Current Approach (.NET)

`PollingConsumer.ClaimTenantsUpfrontAsync()`:
1. Discover all tenants via `GetKnownTenantsAsync()` (queries `SELECT DISTINCT tenant_id FROM events`)
2. Shuffle randomly
3. Try to acquire leases in random order
4. Each replica races to claim — non-deterministic distribution

Problems:
- Random distribution means tenants jump between replicas on restart
- No guarantee of even distribution
- Thundering herd: all replicas race for all tenants at startup (mitigated by jitter, but still inefficient)

## Reference Implementation (TS)

`packages/projections/src/coordinator/tenant-ring.ts`:

```typescript
const VNODES_PER_NODE = 150;

const hashToUint32 = (input: string): number => {
  const digest = createHash('md5').update(input).digest();
  return digest.readUInt32BE(0);
};

export const buildRing = (nodeIds: readonly string[]): VirtualNode[] => {
  const ring: VirtualNode[] = [];
  for (const nodeId of nodeIds) {
    for (let i = 0; i < VNODES_PER_NODE; i++) {
      ring.push({ hash: hashToUint32(`${nodeId}:${i}`), nodeId });
    }
  }
  ring.sort((a, b) => a.hash - b.hash);
  return ring;
};

export const getNodeForTenant = (ring, tenantId): string => {
  const hash = hashToUint32(tenantId);
  // Binary search for first entry >= hash, wrap around
};
```

Persisted assignments in `tenant_assignments` table. `rebalance()` updates assignments when nodes change.

## Implementation Plan

### Step 1: Implement consistent hash ring (pure, no I/O)

```csharp
namespace Alberto.Dcb.Subscriptions;

public static class ConsistentHashRing
{
    private const int VirtualNodesPerNode = 150;

    public record VirtualNode(uint Hash, string NodeId);

    /// <summary>
    /// Build a consistent hash ring from a set of node IDs.
    /// Each node gets 150 virtual nodes for even distribution.
    /// </summary>
    public static IReadOnlyList<VirtualNode> Build(IReadOnlyList<string> nodeIds)
    {
        var ring = new List<VirtualNode>();
        foreach (var nodeId in nodeIds)
        {
            for (var i = 0; i < VirtualNodesPerNode; i++)
            {
                var hash = HashToUInt32($"{nodeId}:{i}");
                ring.Add(new VirtualNode(hash, nodeId));
            }
        }
        ring.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        return ring;
    }

    /// <summary>
    /// Find the node responsible for a given tenant ID.
    /// </summary>
    public static string GetNodeForTenant(IReadOnlyList<VirtualNode> ring, string tenantId)
    {
        if (ring.Count == 0) throw new InvalidOperationException("Ring is empty.");

        var hash = HashToUInt32(tenantId);

        // Binary search for first entry >= hash
        var lo = 0;
        var hi = ring.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (ring[mid].Hash < hash) lo = mid + 1;
            else hi = mid;
        }

        // Wrap around
        var idx = lo >= ring.Count ? 0 : lo;
        return ring[idx].NodeId;
    }

    private static uint HashToUInt32(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
```

### Step 2: Migration — tenant_assignments table

```sql
CREATE TABLE IF NOT EXISTS {schema}.tenant_assignments (
    tenant_id TEXT PRIMARY KEY,
    node_id TEXT NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ring_version BIGINT NOT NULL DEFAULT 1
);
```

### Step 3: `ITenantRing` interface and PostgreSQL implementation

```csharp
public interface ITenantRing
{
    /// <summary>
    /// Rebalance tenant assignments based on current active nodes.
    /// Returns the number of tenants that changed nodes.
    /// </summary>
    Task<int> RebalanceAsync(IReadOnlyList<string> activeNodeIds, CancellationToken ct = default);

    /// <summary>
    /// Get tenants assigned to a specific node.
    /// </summary>
    Task<IReadOnlySet<string>> GetAssignedTenantsAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Register a new tenant and assign it to a node via the hash ring.
    /// No-op if tenant already assigned.
    /// </summary>
    Task RegisterTenantAsync(string tenantId, IReadOnlyList<string> activeNodeIds, CancellationToken ct = default);
}
```

### Step 4: Integrate with PollingConsumer

Replace `ClaimTenantsUpfrontAsync` with hash-ring-based assignment:

```csharp
if (_tenantRing is not null)
{
    // Get active nodes
    var activeNodes = await _tenantProcessorLock.GetActiveReplicasAsync(ConsumerId, ct);

    // Rebalance ring
    await _tenantRing.RebalanceAsync(activeNodes, ct);

    // Get my assigned tenants
    var myTenants = await _tenantRing.GetAssignedTenantsAsync(_replicaId, ct);

    // Only acquire leases for assigned tenants (not random ones)
    foreach (var tenantId in myTenants)
    {
        var lease = await _tenantProcessorLock.TryAcquireForTenantAsync(ConsumerId, tenantId, _replicaId, ct);
        // ...
    }
}
```

### Step 5: Handle new tenants

When `FilterEventsByTenantOwnershipAsync` encounters a new tenant (not in `_ownedTenants`), register it in the hash ring:

```csharp
await _tenantRing.RegisterTenantAsync(tenantId, activeNodes, ct);
var assignedNode = /* check if assigned to us */;
if (assignedNode == _replicaId)
    // Acquire lease and process
```

### Step 6: Periodic rebalance

Add a rebalance check on a timer (e.g., every 30 seconds) that:
1. Gets active nodes
2. Calls `RebalanceAsync`
3. Sheds tenants no longer assigned to this node
4. Claims newly assigned tenants

### Step 7: ConsumerBuilder extension

```csharp
public ConsumerBuilder WithConsistentHashRing()
{
    // Enables hash-ring-based tenant assignment
    // Requires tenant-distributed mode
}
```

## Files to Create

- `src/Alberto.Dcb/Subscriptions/ConsistentHashRing.cs` — pure hash ring logic
- `src/Alberto.Dcb/Subscriptions/ITenantRing.cs` — interface
- `src/Alberto.Dcb.Postgres/PostgresTenantRing.cs` — SQL-backed implementation
- `src/Alberto.Dcb.Postgres/Migrations/016_tenant_assignments.sql`
- `tests/Alberto.Dcb.Tests/Subscriptions/ConsistentHashRingTests.cs`

## Files to Modify

- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — use hash ring for tenant claiming
- Consumer builder — add `WithConsistentHashRing()`

## Acceptance Criteria

- [ ] Hash ring distributes tenants evenly across nodes (within ~10% variance)
- [ ] Adding a node moves only ~1/N of tenants (consistent hashing property)
- [ ] Removing a node redistributes only that node's tenants
- [ ] New tenants are automatically registered and assigned
- [ ] Periodic rebalance detects node changes
- [ ] Unit tests for pure hash ring logic
- [ ] Integration test: 3 nodes, 100 tenants → even distribution
