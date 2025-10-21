# Alberto.EventStore - Core Architecture

This document explains the design patterns and architectural decisions in the Alberto EventStore core library.

## Overview

Alberto.EventStore provides a high-level event store abstraction with pluggable backends, multi-tenancy support, and a
sophisticated subscription system. The library is designed around modularity, allowing different bounded contexts to
have isolated event stores with different configurations.

## Key Design Patterns

### 1. Module-Based Architecture

Each EventStore is a **module** identified by a unique `TEventStore` type. This enables:

- Multiple isolated event stores in the same application
- Different backends for different modules (e.g., Orders in Postgres, Caching in-memory)
- Independent configuration per module
- Type-safe service resolution via keyed services

**Example:**

```csharp
// Define domain-specific event stores
public class OrderEventStore : EventStoreFactory { }
public class PaymentEventStore : EventStoreFactory { }

// Each gets independent configuration
services.AddModule<OrderEventStore>("orders", module => module.WithPostgres(...));
services.AddModule<PaymentEventStore>("payments", module => module.WithPostgres(...));
```

### 2. ModuleBuilder Fluent API

The `ModuleBuilder<TEventStore>` provides a fluent configuration API that enforces correct setup order:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => { ... })              // Backend (required first)
    .WithMultiTenancy<MultiTenantContext>()        // Optional: Multi-tenancy
    .WithChannelSubscriptions(channel => { ... })   // Optional: Subscriptions
    .WithCQRS(assembly)                            // Optional: CQRS integration
    .WithTelemetry());                             // Optional: OpenTelemetry
```

**Why this approach?**

- Clear dependency order: backend must be configured before subscriptions
- Discoverability: IntelliSense guides valid configuration steps
- Type safety: Compile-time guarantees for required configuration
- Extensibility: Easy to add new configuration options

### 3. Factory Pattern for Backend Abstraction

The `EventStoreFactory` base class delegates to backend implementations via `IEventStoreBackend`:

```
EventStoreFactory (per-module, scoped)
    ↓
IEventStoreBackend (interface)
    ↓
InMemoryEventStoreBackend | PostgresEventStoreBackend
```

**Key responsibilities:**

- **EventStoreFactory**: Tenant resolution, diagnostics, channel notifications
- **IEventStoreBackend**: Storage implementation (append, stream)

**Why separate?**

- Backends focus purely on storage mechanics
- Factory handles cross-cutting concerns (telemetry, multi-tenancy)
- Easy to add new backends without touching orchestration logic

### 4. Keyed Services for Module Isolation

Each module registers its services using keyed services with the module key:

```csharp
// Registration (internal)
services.AddKeyedScoped<EventStoreFactory>(moduleKey,
    (sp, key) => ActivatorUtilities.CreateInstance<TEventStore>(sp, ...));

// Consumption
public class OrderHandler(
    [FromKeyedServices(typeof(OrderEventStore).FullName)] OrderEventStore eventStore)
{ }
```

**Benefits:**

- Multiple event store instances with different configurations
- No service registration conflicts between modules
- Type-safe injection via `TEventStore` types

### 5. Multi-Tenancy Abstraction

The `ITenantContext` interface provides tenant isolation:

```csharp
public interface ITenantContext
{
    Tenant Tenant { get; }
}
```

**Implementations:**

- `SingleTenantContext`: Default, single tenant ("default")
- Custom: User-provided (e.g., HTTP header-based, JWT claim-based)

**Integration:**

- Backend receives `Tenant` for every operation
- Storage backends enforce tenant isolation
- Subscriptions can filter by tenant

### 6. Subscription System

Alberto supports two subscription modes:

#### Polling Subscriptions

- Traditional pull-based model
- Backend polls for new events
- Good for cross-service subscriptions

#### Channel Subscriptions (Recommended)

- In-process pub/sub via `System.Threading.Channels`
- Events published to subscribers immediately after append
- Supports three modes:
    - **Sync**: Handler runs inline with append (blocks append)
    - **Async**: Handler runs in background after append (non-blocking)
    - **Hybrid**: Sync for projections, async for everything else

**Architecture:**

```
Append → EventStoreFactory → Backend → ChannelSubscriptionRegistry
                                            ↓
                                    [Sync Handlers → Async Handlers]
```

**Why channels?**

- Ultra-low latency (microseconds vs. milliseconds for polling)
- No database polling overhead
- Strong ordering guarantees
- Built-in backpressure handling

### 7. Telemetry Integration

The diagnostics system is abstracted via `IDiagnosticsEventListener`:

```csharp
public interface IDiagnosticsEventListener
{
    IDisposable Append(IEnumerable<IEventToPersist> events);
    IDisposable Stream(StreamQuery query, int? maxCount);
    Dictionary<string, string> GetTelemetryMetadata();
}
```

**Implementations:**

- `NoopDiagnosticsEventListener`: Default, no overhead
- `ActivityDiagnosticsEventListener`: OpenTelemetry Activities (in Alberto.EventStore.Telemetry)

**Trace context propagation:**

- Telemetry metadata (trace ID, span ID) is automatically embedded in event metadata
- Enables distributed tracing across event-driven workflows

### 8. Stream Queries

Events are queried using `StreamQuery` objects with builder pattern:

```csharp
var query = new StreamQuery()
    .WithEventTypes(new EventType("OrderCreated"), new EventType("OrderPlaced"))
    .WithTags(new EventTag("order", orderId), TagMatchingStrategy.All)
    .FromPosition(1000);
```

**Features:**

- Event type filtering with wildcard support
- Tag-based queries with AND/OR semantics
- Position-based streaming
- Consistency boundaries for optimistic concurrency

## Configuration Patterns

### Minimal Setup (In-Memory)

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory());
```

### Production Setup (Postgres + Multi-Tenancy + Subscriptions)

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options =>
    {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
    })
    .WithMultiTenancy<HttpTenantContext>()
    .WithChannelSubscriptions(channel => channel
        .ConfigureSync(options => options.AllowParallelExecution = true)
        .ConfigureAsync(options => options.MaxRetries = 5)
        .AddPostgresProjection<OrderEventStore, OrderProjection, Guid, Order, OrderProjector>(
            mode: SubscriptionMode.Hybrid))
    .WithTelemetry());
```

## Extension Points

### Adding a New Backend

1. Implement `IEventStoreBackend`
2. Create `ModuleBuilder` extension method:

```csharp
public static ModuleBuilder<TEventStore> WithMyBackend<TEventStore>(
    this ModuleBuilder<TEventStore> builder,
    Action<MyBackendOptions> configure)
{
    // Register backend services
    builder.Services.AddKeyedSingleton<IEventStoreBackend>(
        builder.ModuleKey,
        (sp, key) => new MyEventStoreBackend(...));

    return builder;
}
```

### Adding Custom Telemetry

1. Implement `IDiagnosticsEventListener`
2. Register via `WithTelemetry` or custom extension

### Custom Subscription Types

1. Implement `IHandleEvent<TEvent>` for handlers
2. Add registration via `ChannelSubscriptionsBuilder`

## Performance Considerations

### Backend Selection

- **InMemory**: 30μs single event append, 1.2ms for 1000 events
- **Postgres**: 0.9ms single event append, 18.5ms for 1000 events

### Subscription Modes

- **Sync**: Use only for critical projections (blocks append)
- **Async**: Default for most handlers (non-blocking)
- **Hybrid**: Balance between consistency and performance

### Connection Pooling (Postgres)

- Configure via connection string: `Minimum Pool Size=5;Maximum Pool Size=30`
- Improves performance under high load

## Common Pitfalls

1. **Forgetting to configure backend**: `ModuleBuilder` throws if subscriptions configured before backend
2. **Using sync subscriptions for slow operations**: Blocks event append, impacts throughput
3. **Not configuring multi-tenancy**: Defaults to `SingleTenantContext` if not specified
4. **Schema conflicts in Postgres**: Each module should have its own schema

## File Structure

- `EventStoreFactory.cs` - Base factory class for all event stores
- `ModuleBuilder.cs` - Fluent configuration API
- `IEventStoreBackend.cs` - Backend abstraction interface
- `EventStoreBuilderExtensions.cs` - Legacy builder (deprecated, use ModuleBuilder)
- `Subscriptions/` - Channel and polling subscription infrastructure
- `Events/` - Event envelope, tag, type abstractions
- `MultiTenant/` - Tenant context interfaces
- `Diagnostics/` - Telemetry abstractions
