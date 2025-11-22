# Alberto.EventStore.InMemory - In-Memory Backend

## Overview

The in-memory backend provides a lightweight, thread-safe event store implementation for testing and development. It's
designed for fast feedback loops and deterministic testing without external dependencies.

## Key Features

- **Thread-safe**: All operations use concurrent collections and proper locking
- **Zero setup**: No database or configuration required
- **Fast**: Microsecond-level performance for most operations
- **Deterministic**: Perfect for unit and integration testing
- **Full feature parity**: Implements all EventStore features (consistency checks, tags, etc.)

## Performance Characteristics

**Benchmarks** (from EventStore.Performance.Tests):

- Single event append: ~30μs (30x faster than PostgreSQL)
- 1000-event batch: ~1.2ms (15x faster than PostgreSQL)
- Tag queries: <100μs for 100K+ events

## Usage

### Basic Configuration

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory());
```

### With Multi-Tenancy

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()
    .WithMultiTenancy<MyTenantContext>());
```

### With Subscriptions

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()
    .WithChannelSubscriptions(channel => channel
        .AddProjection<OrderEventStore, OrderProjection, Guid, Order, OrderProjector>(
            mode: SubscriptionMode.Sync)));  // Sync works great for in-memory
```

## Implementation Details

### Data Structures

```csharp
// Events stored in thread-safe list
private readonly List<InMemoryEventEnvelope> _events = new();

// Concurrent access control
private readonly ReaderWriterLockSlim _lock = new();

// Per-tenant indexing for fast queries
private readonly Dictionary<string, List<InMemoryEventEnvelope>> _tenantIndex = new();
```

### Consistency Checks

Implements full optimistic concurrency via in-memory checks:

```csharp
if (consistencyBoundary != null && expectedLastEventId.HasValue)
{
    var matchingEvents = StreamInternal(tenant, consistencyBoundary);
    var actualLastEventId = matchingEvents.MaxBy(e => e.Position)?.Id;

    if (actualLastEventId != expectedLastEventId)
        throw new ConcurrencyConflictException(...);
}
```

### Query Performance

**Tag queries** use in-memory LINQ with optimized predicates:

- `RequireAllTags`: `tags.All(tag => event.Tags.Contains(tag))`
- `RequireAnyTags`: `tags.Any(tag => event.Tags.Contains(tag))`

**Event type queries** use HashSet lookups for O(1) membership checks.

## Testing Patterns

### Unit Tests

```csharp
[Fact]
public async Task Should_append_and_retrieve_events()
{
    // Arrange
    var eventStore = new InMemoryEventStoreBackend();
    var tenant = new Tenant("test");

    // Act
    await eventStore.Append(tenant, events, null, null);
    var retrieved = await eventStore.Stream(tenant, new StreamQuery());

    // Assert
    retrieved.Should().HaveCount(2);
}
```

### Component Tests

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()  // Fast, no DB needed
    .WithChannelSubscriptions(...));

var factory = new WebApplicationFactory<Program>()
    .WithWebHostBuilder(builder => builder.ConfigureServices(services));
```

## Limitations

### Not for Production

- **No persistence**: Data lost on restart
- **No distributed locks**: Single-process only
- **Memory limits**: Unbounded growth for long-running processes
- **No partitioning**: Entire event log in memory

### Use Cases

✅ **Good for:**

- Unit testing business logic
- Component tests with WebApplicationFactory
- Development and prototyping
- Benchmarking and performance comparisons
- CI/CD pipelines (fast, no infrastructure)

❌ **Not good for:**

- Production workloads
- Multi-instance deployments
- Long-running processes
- Large event volumes (>1M events)

## Transitioning to PostgreSQL

Switching from in-memory to PostgreSQL is trivial:

```csharp
// Before (development)
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory());

// After (production)
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options =>
    {
        options.ConnectionString = configuration.GetConnectionString("db");
        options.Schema = "orders";
    }));
```

**No code changes required** - all handlers, projections, and queries work identically.

## File Structure

- `InMemoryEventStoreBackend.cs` - Main backend implementation
- `InMemoryModuleBuilderExtensions.cs` - `.WithInMemory()` extension
- `Subscriptions/InMemoryCheckpointStore.cs` - In-memory checkpoint tracking
- `Subscriptions/InMemoryPoisonPillStore.cs` - In-memory poison pill tracking

## Configuration Options

```csharp
public class InMemoryEventStoreOptions
{
    // Currently minimal - may add capacity limits, eviction policies, etc.
}
```

## Thread Safety

All operations are thread-safe via ReaderWriterLockSlim:

- **Read operations** (Stream): Shared lock, allows concurrent reads
- **Write operations** (Append): Exclusive lock, single writer at a time

**Performance impact:** Negligible for typical workloads (<100 concurrent operations).

## Memory Management

**Growth characteristics:**

- ~1KB per event (varies by payload size)
- 1M events ≈ 1GB memory
- No automatic cleanup (events never deleted)

**Recommendations:**

- Use for tests that create <10K events
- Clear state between tests (`_events.Clear()`)
- Consider PostgreSQL for >100K events

## Comparison with PostgreSQL

| Feature                 | InMemory | PostgreSQL       |
|-------------------------|----------|------------------|
| Single append           | 30μs     | 900μs            |
| Batch append (1000)     | 1.2ms    | 18.5ms           |
| Tag query (100K events) | <100μs   | <10ms            |
| Setup time              | Instant  | 2-5s (migration) |
| Persistence             | No       | Yes              |
| Multi-instance          | No       | Yes              |
| Max events              | ~1M      | Billions         |

## Best Practices

1. **Clear state between tests**: Avoid test pollution
2. **Use for unit tests**: Fast feedback, no infrastructure
3. **Switch to PostgreSQL for integration tests**: Test real persistence
4. **Limit event volume**: Keep tests fast (<1000 events per test)
5. **Use sync subscriptions**: No latency benefit from async in-memory
