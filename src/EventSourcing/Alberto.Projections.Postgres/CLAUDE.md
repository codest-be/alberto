# Alberto.Projections.Postgres - Projection System Architecture

## Overview

The Projections.Postgres package provides JSONB-based projection storage with automatic table creation, integrated
subscription handling, and schema isolation. It bridges the gap between EventStore and read models.

## Key Design Decisions

### 1. JSONB Projection Storage

Projections are stored as JSONB, not individual columns:

```sql
CREATE TABLE {schema}.{projection_name}_projections (
    tenant_id TEXT NOT NULL,
    key TEXT NOT NULL,  -- Guid, int, string, etc.
    state JSONB NOT NULL,
    global_version BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, key)
);
```

**Why JSONB?**

- Schema flexibility: Change projection structure without migrations
- Easy evolution: Add new properties without ALTER TABLE
- Native JSON queries: Use `->` and `->>` operators
- Snapshot storage: Perfect for event-sourced read models

### 2. Automatic Table Creation Per State Type

Each `TState` type gets its own table:

```csharp
services.AddPostgresProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(
    mode: SubscriptionMode.Hybrid);
// Creates table: orders.order_projections

services.AddPostgresProjection<OrderEventStore, OrderStatsSubscription, string, OrderStatistics, OrderStatsProjector>(
    mode: SubscriptionMode.Hybrid);
// Creates table: orders.orderstatistics_projections
```

**Benefits:**

- Type safety: Each projection has its own table
- Clear separation: Easy to understand data model
- Independent scaling: Different retention/indexes per projection

### 3. Subscription-Driven Updates

Projections are updated via event subscriptions, not manual calls:

```csharp
public class OrderProjectionSubscription : IHandleEvent<OrderCreated>, IHandleEvent<OrderPlaced>
{
    public async Task Handle(EventContext<OrderCreated> context)
    {
        var order = new Order { Id = context.Event.OrderId, Status = "Created" };
        await _repository.Upsert(context.Event.OrderId, order, context.GlobalVersion);
    }

    public async Task Handle(EventContext<OrderPlaced> context)
    {
        var order = await _repository.GetByKey(context.Event.OrderId);
        order = order with { Status = "Placed" };
        await _repository.Upsert(context.Event.OrderId, order, context.GlobalVersion);
    }
}
```

**Flow:**

1. Event appended to EventStore
2. Subscription triggered (sync/async/hybrid)
3. Handler updates projection via repository
4. State persisted as JSONB

### 4. Global Version Tracking

Projections track `global_version` for idempotency:

```csharp
public async Task UpdateWithVersion(
    TKey key,
    TState state,
    long globalVersion,
    CancellationToken cancellationToken = default)
{
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = @"
        INSERT INTO {schema}.{table} (tenant_id, key, state, global_version, updated_at)
        VALUES (@TenantId, @Key, @State::jsonb, @GlobalVersion, NOW())
        ON CONFLICT (tenant_id, key)
        DO UPDATE SET
            state = EXCLUDED.state,
            global_version = EXCLUDED.global_version,
            updated_at = NOW()
        WHERE {table}.global_version < EXCLUDED.global_version;";

    await connection.ExecuteAsync(sql, new
    {
        TenantId = _tenantContext.Tenant.Id,
        Key = key.ToString(),
        State = JsonSerializer.Serialize(state),
        GlobalVersion = globalVersion
    });
}
```

**Why global version?**

- **Idempotency**: Replay same event multiple times safely
- **Ordering**: Ensures events applied in correct order
- **Concurrency**: Prevents stale updates from overwriting newer state

### 5. Schema-Level Isolation

Projections use the same schema as their parent EventStore:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => options.Schema = "orders")
    .WithChannelSubscriptions(channel => channel
        .AddPostgresProjection<OrderEventStore, OrderProjection, Guid, Order, OrderProjector>(
            mode: SubscriptionMode.Hybrid)));
// Projection table created in "orders" schema
```

**Benefits:**

- Clear boundaries: All orders data in one schema
- Migration coordination: EventStore + Projections migrate together
- Security: RLS policies applied at schema level

### 6. Subscription Modes

Projections support three subscription modes:

```csharp
public enum SubscriptionMode
{
    Sync,    // Runs inline with append (blocks until projection updated)
    Async,   // Runs in background (non-blocking append)
    Hybrid   // Sync for critical projections, async otherwise
}
```

**Tradeoffs:**

- **Sync**: Strong consistency, blocks append (use sparingly)
- **Async**: High throughput, eventual consistency
- **Hybrid**: Balance between consistency and performance (recommended)

### 7. Migration Hosted Service

Automatic migrations via `IHostedService`:

```csharp
public class ProjectionMigrationHostedService : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var schemas = ProjectionSchemaRegistry.Instance.GetRegisteredSchemas();

        foreach (var (schema, connectionString) in schemas)
        {
            await MigrateSchema(schema, connectionString, cancellationToken);
        }
    }
}
```

**Execution order:**

1. DI container starts
2. `ProjectionMigrationHostedService` runs
3. Schemas created (if `RunMigrations = true`)
4. Tables created on first upsert (lazy creation)
5. App accepts requests

### 8. Projector Pattern

Projectors apply events to state using pattern matching:

```csharp
public class OrderProjector : IProjector<Order>
{
    public Order Apply(Order state, object @event)
    {
        return @event switch
        {
            OrderCreated created => state with
            {
                Id = created.OrderId,
                CustomerId = created.CustomerId,
                Status = OrderStatus.Created
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderShipped shipped => state with
            {
                Status = OrderStatus.Shipped,
                TrackingNumber = shipped.TrackingNumber
            },
            _ => state
        };
    }
}
```

**Benefits:**

- Type-safe event handling
- Immutable state updates
- Easy to test (pure functions)
- Clear event-to-state mapping

## Configuration Patterns

### Development (Auto-Migrate)

```csharp
services.AddPostgresProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>(
    options =>
    {
        options.ConnectionString = "Host=localhost;Database=mydb";
        options.Schema = "orders";
        options.RunMigrations = true;  // Auto-create schema at startup
    });
```

### Production (Manual Migration)

```csharp
services.AddPostgresProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>(
    options =>
    {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
        options.RunMigrations = false;  // Manage via migration pipeline
    });
```

Then run migrations via deployment pipeline or migration script generator.

### Integrated with EventStore Module

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => options.Schema = "orders")
    .WithChannelSubscriptions(channel => channel
        .AddPostgresProjection<OrderEventStore, OrderProjection, Guid, Order, OrderProjector>(
            mode: SubscriptionMode.Hybrid)
        .AddPostgresProjection<OrderEventStore, OrderStatsProjection, string, OrderStats, OrderStatsProjector>(
            mode: SubscriptionMode.Async)));
```

## Performance Considerations

### Upsert Performance

- **JSONB overhead**: ~10-15% vs. normalized columns
- **Index on tenant_id+key**: Primary key, instant lookups
- **Bulk updates**: Use batching for high-volume projections

### Query Performance

- **Get by key**: O(1) via primary key index
- **JSON queries**: Use GIN indexes for complex queries
- **Tenant isolation**: Indexed, no performance penalty

### Subscription Impact

- **Sync mode**: Adds ~5-10ms to append time
- **Async mode**: Zero append overhead
- **Hybrid mode**: 2-3ms for critical projections

## Common Pitfalls

1. **Forgetting global version**: Always pass `context.GlobalVersion` to `UpdateWithVersion`
2. **Not using schema**: Schema is required, no default
3. **Sync subscriptions for slow operations**: Use async or hybrid for heavy processing
4. **Missing projector logic**: Projection won't update if event not handled

## File Structure

- `PostgresProjectionRepository.cs` - Main repository implementation
- `PostgresProjectionOptions.cs` - Configuration options
- `PostgresProjectionBuilderExtensions.cs` - Subscription integration
- `Migrations/ProjectionMigrationHostedService.cs` - Auto-migration service
- `ProjectionSchemaRegistry.cs` - Schema tracking singleton

## Query Patterns

### Get by Key

```csharp
var order = await repository.GetByKey(orderId, cancellationToken);
```

### Get All (with pagination)

```csharp
var orders = await repository.GetAll(skip: 0, take: 100, cancellationToken);
```

### JSON Queries (via Dapper)

```csharp
const string sql = @"
    SELECT state
    FROM orders.order_projections
    WHERE tenant_id = @TenantId
      AND state->>'Status' = 'Shipped'
    LIMIT 100;";

var shippedOrders = await connection.QueryAsync<Order>(sql, new { TenantId });
```

## Testing Strategy

### Unit Tests

Test projectors in isolation:

```csharp
[Fact]
public void Should_set_status_to_placed_when_order_placed()
{
    var projector = new OrderProjector();
    var state = new Order { Id = orderId, Status = OrderStatus.Created };

    var result = projector.Apply(state, new OrderPlaced { OrderId = orderId });

    result.Status.Should().Be(OrderStatus.Placed);
}
```

### Integration Tests

Test full subscription pipeline with in-memory repository.
