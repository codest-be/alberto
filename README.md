# Alberto EventStore

A high-performance event store library for .NET with multi-tenant and multi-schema support.

## Installation

Alberto EventStore is available as NuGet packages:

```bash
# Core library (required)
dotnet add package Alberto.EventStore

# In-memory implementation (for testing/development)
dotnet add package Alberto.EventStore.InMemory

# PostgreSQL implementation (for production)
dotnet add package Alberto.EventStore.Postgres

# Telemetry support (optional)
dotnet add package Alberto.EventStore.Telemetry
```

### Package Versions

Current version: **0.0.1-alpha**

⚠️ **Preview Warning**: This is an alpha preview release. Breaking changes may occur at any point until version 1.0.0.
Use with caution in production environments.

Note: The packages target .NET 9.0 and .NET 10.0 RC - stable packages will be released when .NET 10.0 is generally available.

## Requirements

- **.NET**: 9.0 or 10.0+
- **PostgreSQL**: 15+ (16+ recommended for production)
- **Tested on**: PostgreSQL 15, 16, 17

## Quick Start

### In-Memory Setup (Testing/Development)

```csharp
using Alberto.EventStore;
using Alberto.EventStore.InMemory;

// Configure services
services.AddEventStore()
        .AddInMemoryEventStore();

// Use the event store
public class OrderService
{
    private readonly IEventStore _eventStore;

    public OrderService(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async Task CreateOrder(CreateOrderCommand command)
    {
        var events = new[]
        {
            new OrderCreated(command.OrderId, command.CustomerId),
            new OrderItemAdded(command.OrderId, command.ProductId, command.Quantity)
        };

        await _eventStore.AppendAsync("orders", command.OrderId.ToString(), events);
    }
}
```

### PostgreSQL Setup (Production)

**Requirements**: PostgreSQL 15+ (PostgreSQL 16+ recommended for optimal performance)

```csharp
using Alberto.EventStore;
using Alberto.EventStore.Postgres;

// Configure services
services.AddEventStore()
        .AddPostgresEventStore("orders", options =>
        {
            options.ConnectionString = connectionString;
            options.Schema = "orders_events";
        });
```

## Features

- **Multi-Backend Architecture**: In-memory and PostgreSQL implementations
- **Multi-Tenant Support**: Isolated event streams per tenant
- **Multi-Schema Support**: Schema-based logical separation in PostgreSQL
- **Optimistic Concurrency**: Consistency boundaries with expected event IDs
- **High Performance**: Optimized bulk operations and connection pooling
- **Docker Integration**: Full Testcontainers support for testing

## Testing

The project uses a two-tier testing approach:

### Fast Feedback Tests (`EventStore.Tests`)

- **109 tests** running in ~3 seconds
- Unit and integration tests for correctness
- Runs on every push/PR for immediate feedback
- Command: `dotnet test EventStore.Tests`

### Performance Analysis (`EventStore.Performance.Tests`)

- **78 benchmarks** using BenchmarkDotNet
- Comprehensive performance analysis and regression detection
- Separate CI pipeline to preserve GitHub Actions minutes
- Command: `dotnet run --project EventStore.Performance.Tests --configuration Release`

## Performance

Recent benchmarks show excellent performance characteristics:

- **InMemory**: Single event append ~30μs, 1000-event batch ~1.2ms
- **PostgreSQL**: Single event append ~0.9ms, 1000-event batch ~18.5ms
- **Scalability**: Performance gap decreases with larger batches (15x vs 30x)

See [Performance Tests README](EventStore.Performance.Tests/README.md) for detailed benchmarks.

## CI/CD

- **Main Pipeline**: Fast build and test on every push/PR (~1-2 minutes)
- **Performance Pipeline**: Weekly performance analysis and regression detection (manual trigger available)