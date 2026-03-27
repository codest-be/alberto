# Plan 07: Optional Tenancy as a Decorator

## Goal
Make the event store **tenant-free by default**. Tenancy becomes an opt-in decorator via `.WithTenancy()`. This eliminates the performance overhead of tenant-scoped indexes and queries for single-tenant applications. The same decorator pattern can later be extended to other contextual dimensions (e.g., `.WithUserId()`).

## The Problem

Currently, `tenant_id` is deeply baked in:
- `events` table: `tenant_id VARCHAR(100) NOT NULL` on every row
- Inverted indexes: PKs are `(tenant_id, event_type, global_position)` and `(tenant_id, tag, global_position)` — tenant is the leading column
- Every SQL function takes `p_tenant_id` and filters by it
- `IEventStoreBackend.Stream()` requires `tenantId` as the first parameter
- `IEventEnvelope.TenantId` is always present

For single-tenant apps, this means:
- **Index bloat**: Every index entry carries a redundant "default" tenant value as the leading key component
- **Query overhead**: Every query includes `WHERE tenant_id = '_'` that never eliminates rows but still costs a comparison
- **Wider index scans**: The leading `tenant_id` column in composite indexes means PostgreSQL can't do a clean `(event_type, global_position)` range scan — it needs `(tenant_id, event_type, global_position)` which is deeper
- **API friction**: Every caller must pass a tenantId they don't care about

## Design: Decorator Pattern

```
┌─────────────────────────────────────────┐
│  Single-tenant app                      │
│                                         │
│  IEventStoreBackend                     │
│    └─ PostgresEventStoreBackend (base)  │
│         - No tenant_id filtering        │
│         - Simpler indexes               │
│         - Simpler SQL functions          │
└─────────────────────────────────────────┘

┌──────────────────────────────────────────────────┐
│  Multi-tenant app                                │
│                                                  │
│  IEventStoreBackend                              │
│    └─ TenantEventStoreDecorator                  │
│         - Reads tenantId from TenantAccessor     │
│         - Injects into calls                     │
│         └─ PostgresTenantEventStoreBackend       │
│              - tenant_id filtering               │
│              - Tenant-scoped indexes             │
│              - Tenant-scoped SQL functions        │
└──────────────────────────────────────────────────┘
```

### Configuration

```csharp
// Single-tenant (default) — clean, simple, fast
services.AddAlberto("orders", builder => builder
    .WithPostgres(options => { ... })
);

// Multi-tenant (opt-in)
services.AddAlberto("orders", builder => builder
    .WithPostgres(options => { ... })
    .WithTenancy()
);

// Future: other context decorators
services.AddAlberto("orders", builder => builder
    .WithPostgres(options => { ... })
    .WithTenancy()
    .WithUserContext()  // Adds user_id to events, available on envelope
);
```

## Implementation Plan

### Step 1: Make `IEventStoreBackend` tenant-free

Remove `tenantId` from the base interface. This is a breaking change but the right long-term move.

```csharp
public interface IEventStoreBackend
{
    /// <summary>
    /// Reads events matching the query.
    /// In multi-tenant mode, automatically scoped to the current tenant.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all events (for subscriptions/projections).
    /// In multi-tenant mode, returns events across all tenants.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends events with optional DCB consistency check.
    /// In multi-tenant mode, automatically tagged with current tenant.
    /// </summary>
    Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last position.
    /// In multi-tenant mode, scoped to current tenant.
    /// </summary>
    Task<long> GetLastPosition(
        CancellationToken cancellationToken = default);
}
```

Note: `StreamGlobal` becomes `StreamAll` (clearer naming when there's no tenant concept). `GetLastPositionGlobal` can be a separate method on a tenant-aware extension.

### Step 2: Tenant-aware interface (extension of base)

```csharp
/// <summary>
/// Extended interface for multi-tenant event stores.
/// Adds cross-tenant operations not available on the base interface.
/// </summary>
public interface ITenantEventStoreBackend : IEventStoreBackend
{
    /// <summary>
    /// Gets the last position across all tenants.
    /// </summary>
    Task<long> GetLastPositionGlobal(CancellationToken ct = default);
}
```

Actually — the PollingConsumer always uses `StreamAll` and `GetLastPosition` (global). In single-tenant mode, `StreamAll` and `Stream(DcbQuery.Empty)` return the same thing, and `GetLastPosition` is already global. So the base interface is sufficient for the consumer.

In multi-tenant mode:
- `Stream(query)` = scoped to current tenant (via TenantAccessor)
- `StreamAll()` = cross-tenant (used by PollingConsumer)
- `GetLastPosition()` = scoped to current tenant
- Need: `GetLastPositionGlobal()` for the consumer

So the consumer needs a way to get the global position regardless of tenant mode. Options:
- Base interface: `GetLastPosition()` always returns global in single-tenant (because there's only one)
- Multi-tenant: decorator provides scoped version; add `GetLastPositionGlobal()` as separate concern

Simplest: keep it on the base interface.

```csharp
public interface IEventStoreBackend
{
    Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query, long afterPosition = 0, int? limit = null,
        CancellationToken ct = default);

    Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0, int? limit = null,
        CancellationToken ct = default);

    Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null, long? expectedPosition = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the last (highest) global position across all events.
    /// </summary>
    Task<long> GetLastPosition(CancellationToken ct = default);
}
```

In single-tenant mode: `Stream` and `StreamAll` differ only by whether a `DcbQuery` filter is applied.

In multi-tenant mode: `Stream` is tenant-scoped (decorator injects tenant filter), `StreamAll` is cross-tenant.

### Step 3: `IEventEnvelope` — make TenantId optional

```csharp
public interface IEventEnvelope
{
    Guid Id { get; }
    string? TenantId { get; }           // nullable — null in single-tenant mode
    long GlobalPosition { get; }
    EventType EventType { get; }
    IReadOnlyCollection<EventTag> Tags { get; }
    string EventData { get; }
    IReadOnlyDictionary<string, string> Metadata { get; }
    DateTime CreatedAt { get; }
}
```

### Step 4: Single-tenant PostgreSQL backend

New backend class that uses simplified SQL:

```csharp
public sealed class PostgresEventStoreBackend : IEventStoreBackend
{
    // Uses functions WITHOUT tenant_id parameters:
    // - append_events(p_events, p_dcb_types, p_dcb_tags, p_expected_position)
    // - read_by_types(p_types, p_after_position, p_limit)
    // - read_by_tags(p_tags, p_after_position, p_limit)
    // - read_all(p_after_position, p_limit)
    // - get_last_position()
}
```

### Step 5: Single-tenant SQL migrations

New migration set (or parameterized existing ones) that creates:

```sql
-- Events table — NO tenant_id column
CREATE TABLE IF NOT EXISTS $schema_prefix$events (
    global_position   BIGSERIAL PRIMARY KEY,
    event_id          UUID NOT NULL DEFAULT gen_random_uuid(),
    event_type        VARCHAR(500) NOT NULL,
    event_tags        VARCHAR(500)[] NOT NULL DEFAULT '{}',
    event_data        JSONB NOT NULL DEFAULT '{}',
    event_metadata    JSONB NOT NULL DEFAULT '{}',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (event_id)
);

-- Inverted indexes — NO tenant_id in key (smaller, faster)
CREATE TABLE IF NOT EXISTS $schema_prefix$event_type_positions (
    event_type        VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (event_type, global_position)
);

CREATE TABLE IF NOT EXISTS $schema_prefix$event_tag_positions (
    tag               VARCHAR(500) NOT NULL,
    global_position   BIGINT NOT NULL REFERENCES $schema_prefix$events(global_position) ON DELETE CASCADE,
    PRIMARY KEY (tag, global_position)
);
```

SQL functions drop all `tenant_id` parameters and filters:

```sql
-- Simplified: no tenant_id filtering
CREATE OR REPLACE FUNCTION $schema_prefix$read_by_tags(
    p_tags VARCHAR(500)[],
    p_after_position BIGINT DEFAULT 0,
    p_limit INT DEFAULT NULL
) RETURNS TABLE (...) AS $$
BEGIN
    RETURN QUERY
    SELECT e.global_position, e.event_id, e.event_type, e.event_tags,
           e.event_data, e.event_metadata, e.created_at
    FROM $schema_prefix$events e
    INNER JOIN $schema_prefix$event_tag_positions etagp
        ON e.global_position = etagp.global_position
    WHERE etagp.tag = ANY(p_tags)
      AND e.global_position > p_after_position
    ORDER BY e.global_position
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;
```

### Step 6: Tenant decorator

```csharp
/// <summary>
/// Decorator that adds tenant scoping to an event store backend.
/// Reads tenant from TenantAccessor and injects it into queries.
/// </summary>
internal sealed class TenantEventStoreDecorator : IEventStoreBackend
{
    private readonly PostgresTenantEventStoreBackend _inner;
    private readonly ITenantAccessor _tenantAccessor;

    public async Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query, long afterPosition = 0, int? limit = null,
        CancellationToken ct = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return await _inner.StreamForTenant(tenantId, query, afterPosition, limit, ct);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0, int? limit = null,
        CancellationToken ct = default)
    {
        // Cross-tenant: no tenant filter
        return await _inner.StreamAllTenants(afterPosition, limit, ct);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events, DcbQuery? dcbQuery = null,
        long? expectedPosition = null, CancellationToken ct = default)
    {
        var tenantId = _tenantAccessor.TenantId;
        return await _inner.AppendForTenant(tenantId, events, dcbQuery, expectedPosition, ct);
    }

    public async Task<long> GetLastPosition(CancellationToken ct = default)
    {
        // In multi-tenant mode, GetLastPosition is global (used by consumer)
        return await _inner.GetLastPositionGlobal(ct);
    }
}
```

### Step 7: `PostgresTenantEventStoreBackend`

This is essentially the current `PostgresEventStoreBackend` renamed. It uses the existing tenant-scoped SQL functions. It exposes tenant-specific methods that the decorator calls.

### Step 8: Builder integration

```csharp
public class DcbModuleBuilder
{
    private bool _withTenancy;

    /// <summary>
    /// Enables multi-tenant mode. Tenant ID is resolved from TenantAccessor
    /// and used to scope all event store operations.
    /// Without this, the event store operates in single-tenant mode with
    /// simpler indexes and no tenant filtering overhead.
    /// </summary>
    public DcbModuleBuilder WithTenancy()
    {
        _withTenancy = true;
        return this;
    }
}
```

In `WithPostgres()`:
```csharp
if (_withTenancy)
{
    // Register PostgresTenantEventStoreBackend + TenantEventStoreDecorator
    // Run multi-tenant migrations
    // Register TenantContext + TenantAccessor
}
else
{
    // Register PostgresEventStoreBackend (simple)
    // Run single-tenant migrations
    // No TenantContext/TenantAccessor needed
}
```

### Step 9: Migration strategy

The migrator needs to know which mode to use. Options:

**Option A**: Two separate migration folders (`Migrations/SingleTenant/` and `Migrations/MultiTenant/`)
**Option B**: Single migration files with conditional SQL via DbUp variables (`$with_tenancy$`)
**Option C**: Generate SQL programmatically based on configuration

Recommend **Option A** — clearest, no conditionals in SQL, each set is independently testable.

### Step 10: Update PollingConsumer

The consumer uses `StreamAll` (formerly `StreamGlobal`) and `GetLastPosition` (formerly `GetLastPositionGlobal`). In single-tenant mode:
- No tenant filtering needed
- No tenant leases needed
- No tenant distribution needed
- `_distributionMode` defaults to `SingleLeader` (or none)

The consumer code simplifies naturally because all the tenant-distributed code paths only activate when `_distributionMode == TenantDistributed`, which requires `.WithTenancy()`.

### Step 11: Update IEventStore (high-level)

Same pattern — remove tenantId from `AppendAsync` and `StreamAsync`:

```csharp
public interface IEventStore
{
    Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null, long? expectedPosition = null,
        CancellationToken ct = default);

    Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
        DcbQuery query, long afterPosition = 0, int? limit = null,
        CancellationToken ct = default);

    // ...
}
```

### Step 12: Update InMemoryEventStoreBackend

Create two variants:
- `InMemoryEventStoreBackend` — single-tenant (no tenant_id in indexes)
- `InMemoryTenantEventStoreBackend` — multi-tenant (current behavior)

### Step 13: Future — `.WithUserContext()` decorator

The same pattern extends to other contextual dimensions:

```csharp
public DcbModuleBuilder WithUserContext()
{
    // Adds user_id column to events table
    // Registers IUserAccessor
    // Decorator reads userId from IUserAccessor and stores in event metadata/column
    // Available on IEventEnvelope.UserId
}
```

This is out of scope for this plan but the decorator infrastructure supports it.

## Impact Assessment

### Files to create
- `src/Alberto.Dcb.Postgres/PostgresEventStoreBackend.cs` — rename current to `PostgresTenantEventStoreBackend`, create new single-tenant version
- `src/Alberto.Dcb.Postgres/PostgresTenantEventStoreBackend.cs` — renamed from current
- `src/Alberto.Dcb/TenantEventStoreDecorator.cs` — decorator
- `src/Alberto.Dcb.Postgres/Migrations/SingleTenant/` — new migration folder
- `src/Alberto.Dcb.InMemory/InMemoryEventStoreBackend.cs` — simplify to single-tenant default

### Files to modify (interface changes ripple through)
- `src/Alberto.Dcb/IEventStoreBackend.cs` — remove tenantId params
- `src/Alberto.Dcb/IEventStore.cs` — remove tenantId params
- `src/Alberto.Dcb/IEventEnvelope.cs` — make TenantId nullable
- `src/Alberto.Dcb/Subscriptions/PollingConsumer.cs` — use new interface
- `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs` — adapt to optional tenantId
- `src/Alberto.Dcb/Subscriptions/ProjectionContext.cs` — nullable tenantId
- `src/Alberto.Dcb/Subscriptions/IProjectionEntity.cs` — nullable TenantId
- `src/Alberto.Dcb/Append/InterceptingEventStoreBackend.cs` — adapt
- `src/Alberto.Dcb.EntityFramework/EfStateStore.cs` — adapt
- `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs` — conditional registration
- `src/Alberto.Dcb.Admin/` — adapt admin queries
- `apps/Alberto.Orders/` — update to use new API
- All test files

### Breaking changes
- `IEventStoreBackend` signature changes (tenantId removed)
- `IEventStore` signature changes
- `IEventEnvelope.TenantId` becomes nullable
- `IProjectionEntity.TenantId` becomes nullable
- Consumers must call `.WithTenancy()` to get multi-tenant behavior

## Performance Impact (single-tenant)

| Aspect | Before | After |
|--------|--------|-------|
| Inverted index PK | `(tenant_id, event_type, global_position)` | `(event_type, global_position)` |
| Index key size | ~110 bytes per entry | ~10-60 bytes per entry (no tenant prefix) |
| Index scan | 3-column composite | 2-column composite (faster range scans) |
| Query WHERE | `WHERE tenant_id = '_' AND type = ANY(...)` | `WHERE type = ANY(...)` |
| events index | `(tenant_id, global_position)` | None needed (PK suffices) |
| SQL function params | 5+ params with tenant | 4+ params without tenant |

## Acceptance Criteria

- [ ] Single-tenant mode works without any tenantId parameter
- [ ] Single-tenant SQL has no tenant_id column in inverted indexes
- [ ] Single-tenant SQL functions don't filter by tenant_id
- [ ] Multi-tenant mode works identically to current behavior via `.WithTenancy()`
- [ ] TenantAccessor automatically injects tenantId in multi-tenant mode
- [ ] PollingConsumer works in both modes
- [ ] InMemory backend works in both modes
- [ ] Existing multi-tenant tests pass with `.WithTenancy()` enabled
- [ ] New single-tenant tests pass without `.WithTenancy()`
- [ ] Orders sample updated to use `.WithTenancy()` explicitly
