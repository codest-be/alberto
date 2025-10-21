# Alberto.EventStore.Postgres - PostgreSQL Backend

This document explains the design and implementation of the PostgreSQL backend for Alberto EventStore.

## Overview

The Postgres backend provides production-ready event persistence with high performance, JSONB storage, connection
pooling, and automatic schema management. It's designed to handle millions of events with microsecond-level query
performance.

## Key Design Decisions

### 1. Hybrid Storage Strategy

Events use a hybrid approach combining structured columns, TEXT arrays, and JSONB:

```sql
CREATE TABLE {schema}.events (
                                 position
                                 BIGSERIAL
                                 PRIMARY
                                 KEY,
                                 id
                                 UUID
                                 NOT
                                 NULL
                                 UNIQUE,
                                 tenant_id
                                 VARCHAR
(
                                 20
) NOT NULL,
    event_type TEXT NOT NULL,
    data JSONB NOT NULL, -- Event payload as JSONB
    tags TEXT [] NOT NULL, -- Tags as PostgreSQL array
    created_at TIMESTAMPTZ NOT NULL,
    metadata JSONB NOT NULL -- Metadata (telemetry, etc.)
);
```

**Why this approach?**

- **JSONB for event data**: Schema flexibility for evolving events
- **TEXT[] for tags**: Native array operations and GIN indexing for fast filtering
- **Structured columns**: High-performance queries on position, tenant, event_type

**JSONB Benefits:**

- Schema flexibility: Add new event properties without migrations
- High performance: PostgreSQL JSONB is optimized with GIN indexing
- Native JSON operations: Query event data with `->` and `->>` operators
- Storage efficiency: Binary format reduces storage overhead

**TEXT[] for Tags Benefits:**

- Fast array operations: `tags && ARRAY['order:123']` for overlap checks
- GIN indexing: Optimized for tag-based queries
- Native PostgreSQL support: No JSON parsing overhead
- Clear semantics: Tags are clearly typed as arrays

### 2. Bulk Insert Threshold Optimization

The backend uses **adaptive bulk inserts** based on event count:

```csharp
public int BulkInsertThreshold { get; set; } = 5;  // Default: 5 events
```

**How it works:**

- **< 5 events**: Individual INSERT statements in a single transaction
- **≥ 5 events**: PostgreSQL `COPY` command (bulk insert)
- Benchmark-proven optimal threshold (see performance tests)

**Why 5?**

- Performance tests showed **14% improvement** over threshold=1
- Balances overhead vs. batch efficiency
- Higher thresholds (50+) add overhead without benefit

**Use cases:**

- Small appends (1-4 events): Low latency, minimal overhead
- Large batches (10-1000+ events): Maximum throughput via COPY

### 3. Connection Pooling

PostgreSQL connection pooling is configured via connection string parameters:

```csharp
options.ConnectionString =
    $"{baseConnectionString};Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300;Connection Pruning Interval=10";
```

**Key parameters:**

- `Minimum Pool Size=5`: Always keep 5 connections warm
- `Maximum Pool Size=30`: Cap at 30 connections per module
- `Connection Idle Lifetime=300`: Recycle idle connections after 5 minutes
- `Connection Pruning Interval=10`: Check for idle connections every 10 seconds

**Why this configuration?**

- Prevents connection exhaustion under high load
- Reduces connection creation overhead (warm pool)
- Avoids PostgreSQL max_connections limit issues
- Automatically prunes stale connections

**Best practices:**

- **Development**: Min=2, Max=10 (low resource usage)
- **Production**: Min=5, Max=30 per module (high throughput)
- **High-scale**: Adjust based on concurrent workload and PostgreSQL settings

### 4. Multi-Schema Architecture

Each module uses its **own PostgreSQL schema** for logical isolation:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => options.Schema = "orders"));

services.AddModule<PaymentEventStore>("payments", module => module
    .WithPostgres(options => options.Schema = "payments"));
```

**Schema structure:**

```
Database: myapp
├── orders (schema)
│   ├── events
│   ├── checkpoints
│   └── poison_pills
├── payments (schema)
│   ├── events
│   ├── checkpoints
│   └── poison_pills
```

**Benefits:**

- **Bounded context isolation**: Clear separation between domains
- **Independent scaling**: Different retention policies, indexes per schema
- **Security**: Row-level security policies per schema
- **Migration safety**: Schema changes don't affect other modules

### 5. Schema Registry and Automatic Migrations

The `PostgresSchemaRegistry` singleton tracks all schemas across modules:

```csharp
public class PostgresSchemaRegistry
{
    public static PostgresSchemaRegistry Instance { get; } = new();

    public void Register(string moduleKey, string connectionString) { }
    public HashSet<string> GetRegisteredSchemas() { }
}
```

**How it works:**

1. Each `WithPostgres()` call registers its schema
2. `EventStoreMigrationHostedService` (if enabled) discovers all schemas
3. Migrations run at application startup via `IEventStoreMigration` implementations

**Migration modes:**

- **Automatic (Development)**: Set `RunMigrations = true`, schemas created at startup
- **Manual (Production)**: Use migration script generator or deployment pipeline

### 6. Optimistic Concurrency with Consistency Boundaries

The backend enforces **optimistic concurrency** via consistency boundaries:

```csharp
await eventStore.Append(
    events,
    consistencyBoundary: query,      // Stream to check for conflicts
    expectedLastEventId: lastEventId // Expected last event ID
);
```

**Implementation:**

```csharp
// Check consistency
if (consistencyBoundary != null && expectedLastEventId.HasValue)
{
    var existingEvents = await Stream(tenant, consistencyBoundary, maxCount: 1);
    var actualLastEventId = existingEvents.MaxBy(e => e.Position)?.Id;

    if (actualLastEventId != expectedLastEventId)
        throw new ConcurrencyConflictException(...);
}
```

**Use cases:**

- Prevent lost updates (e.g., two users editing same order)
- Enforce version-based updates in event sourcing
- Guarantee event ordering within a stream

### 7. Subscription Infrastructure

The Postgres backend provides full infrastructure for both polling and channel subscriptions:

#### Checkpoint Store

- **PostgresCheckpointStore**: Persists subscription positions
- **ThrottledCheckpointStore**: Batches checkpoint writes (default: 5 seconds)

```csharp
CREATE TABLE {schema}.checkpoints (
    subscription_id TEXT PRIMARY KEY,
    position BIGINT NOT NULL,
    last_updated TIMESTAMPTZ NOT NULL
);
```

#### Poison Pill Store

- **PostgresPoisonPillStore**: Tracks failed event processing
- **CachedPoisonPillStore**: In-memory cache to reduce DB queries

```csharp
CREATE TABLE {schema}.poison_pills (
    subscription_id TEXT NOT NULL,
    position BIGINT NOT NULL,
    event_id UUID NOT NULL,
    error_message TEXT,
    created TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (subscription_id, position)
);
```

#### Distributed Locking

- **PostgresAdvisoryLock**: Prevents multiple instances from polling same subscription
- Uses PostgreSQL advisory locks: `pg_try_advisory_lock()`

**Why advisory locks?**

- No external dependencies (Redis, etc.)
- Atomic lock acquisition
- Automatic release on connection close
- Perfect for horizontal scaling

### 8. Transaction Context Support

The backend supports **ambient transactions** via `TransactionContext`:

```csharp
using (var tx = new TransactionContext(connection, transaction))
{
    await eventStore.Append(...);  // Participates in transaction
    await projectionRepo.Update(...);  // Same transaction
}
```

**Use cases:**

- Atomic append + projection update
- Cross-module transactions (same connection)
- Integration with external transaction coordinators

**How it works:**

- Check for ambient `TransactionContext.Current`
- If present, reuse connection/transaction
- If absent, create new connection

### 9. Indexing Strategy

The backend creates **optimized covering indexes** for different query patterns:

```sql
-- Consistency check index
CREATE INDEX idx_{schema} _events_tenant_id
    ON {schema}.events (tenant_id, id);

-- Primary covering index for tenant-scoped queries
CREATE INDEX idx_{schema} _events_tenant_all
    ON {schema}.events (tenant_id, position)
    INCLUDE (event_type, tags, id, data, metadata, created_at);

-- Global position index for subscriptions
CREATE INDEX idx_{schema} _events_global_position
    ON {schema}.events (position)
    INCLUDE (id, tenant_id, event_type, tags, data, metadata, created_at);

-- Event type filtering
CREATE INDEX idx_{schema} _events_tenant_type_position
    ON {schema}.events (tenant_id, event_type, position);
```

**Why covering indexes (INCLUDE clause)?**

- **Index-only scans**: PostgreSQL returns data without touching heap table
- **Reduced I/O**: All required columns are in the index
- **Better caching**: Smaller index pages stay in cache longer

**Query patterns optimized:**

- `Stream(tenant)`: Uses `tenant_all` with index-only scan
- `StreamAll(fromPosition)`: Uses `global_position` for subscriptions
- `Stream(tenant, eventType)`: Uses `tenant_type_position` when selective
- `Consistency checks`: Uses `tenant_id` index
- Tag queries: Implicitly uses GIN index on TEXT[] column

**Performance impact:**

- Tenant queries: **100x faster** (full table scan → index-only scan)
- Event type filtering: **50x faster**
- Tag queries: **10-20x faster** (GIN on TEXT[])
- Subscription queries: **Near-instant** via covering index

**Trade-offs:**

- ✅ Sub-millisecond query performance
- ✅ Scales to millions of events
- ✅ Index-only scans eliminate heap access
- ❌ ~15% insert overhead (index maintenance)
- ❌ Increased storage (~30-40% for covering indexes)

### 10. Metrics and Diagnostics

The backend integrates with `IMetricsRecorder` for observability:

```csharp
_metrics.RecordAppend(tenantId, schema, eventCount);
_metrics.RecordEventsQueried(resultCount, schema, hasFilters);
_metrics.RecordQuery(schema, hasFilters);
```

**Metrics collected:**

- Append operations (count, duration, tenant, schema)
- Query operations (count, duration, filter usage)
- Events appended (per type, per tenant)
- Events queried (per query type)

**Integration:**

- OpenTelemetry: Automatic via `Alberto.EventStore.Telemetry`
- Custom: Implement `IMetricsRecorder`

## Performance Characteristics

### Benchmarks (from EventStore.Performance.Tests)

**Single Event Append:**

- Without pooling: ~878μs
- With pooling: ~967μs
- InMemory (baseline): ~30μs

**Batch Append (1000 events):**

- Postgres: ~18.5ms
- InMemory: ~1.2ms
- **Scaling**: 15x slower (vs. 30x for single events)

**Key takeaways:**

- Postgres excels at batch operations
- Connection pooling adds slight overhead for single operations
- Use batching for high-throughput scenarios

### Query Performance

**Tag queries (optimized):**

- 1K events: <3ms
- 10K events: <15ms
- 100K events: <10ms (GIN index shines)

**Event type queries:**

- Simple filter: <5ms for 100K+ events
- Complex filters: <50ms for 1M+ events

## Configuration Patterns

### Minimal Configuration

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options =>
    {
        options.ConnectionString = "Host=localhost;Database=mydb;Username=user;Password=pass";
        options.Schema = "orders";
    }));
```

### Production Configuration

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options =>
    {
        var baseConnectionString = configuration.GetConnectionString("db");
        options.ConnectionString =
            $"{baseConnectionString};Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300";
        options.Schema = "orders";
        options.BulkInsertThreshold = 5;  // Optimal for most workloads
    }));
```

### High-Throughput Configuration

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options =>
    {
        var baseConnectionString = configuration.GetConnectionString("db");
        options.ConnectionString =
            $"{baseConnectionString};Minimum Pool Size=10;Maximum Pool Size=50;Connection Idle Lifetime=600";
        options.Schema = "orders";
        options.BulkInsertThreshold = 10;  // Higher threshold for large batches
    }));
```

## Common Pitfalls

1. **Not specifying schema**: Always set `options.Schema` to avoid conflicts
2. **Over-pooling**: Max pool size too high can exhaust PostgreSQL max_connections
3. **Under-pooling**: Min pool size too low causes connection creation overhead
4. **Missing indexes**: Ensure GIN index on tags for tag-based queries
5. **Large single appends**: Use batching for >100 events to leverage COPY optimization

## File Structure

- `PostgresEventStoreBackend.cs` - Core backend implementation
- `PostgresModuleBuilderExtensions.cs` - ModuleBuilder integration
- `PostgresEventStoreOptions.cs` - Configuration options
- `PostgresSchemaRegistry.cs` - Schema registration singleton
- `Migrations/` - Schema migration infrastructure
- `Subscriptions/` - Checkpoint, poison pill, and distributed locking stores

## Migration Strategy

### Development

```csharp
// Auto-migrate at startup (via IEventStoreMigration)
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => { ... }));
```

### Production

Use the migration script generator:

```bash
dotnet run --project src/Tools/Alberto.MigrationScriptGenerator -- \
  --schemas orders,payments \
  --output ./migrations
```

Then apply via your deployment pipeline (Flyway, Liquibase, psql, etc.).

## Security Considerations

1. **Schema isolation**: Each module's events are isolated by schema
2. **Connection string security**: Use Azure Key Vault, AWS Secrets Manager, etc.
3. **Row-level security**: Implement PostgreSQL RLS policies for tenant isolation
4. **SQL injection protection**: Uses parameterized queries (Dapper) throughout
