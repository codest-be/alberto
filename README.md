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

Current version: **Automatically versioned with GitVersion**

⚠️ **Preview Warning**: This is a preview release. Breaking changes may occur at any point until version 1.0.0.
Use with caution in production environments.

Note: The packages target .NET 10.0.

## Requirements

- **.NET**: 10.0+
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

## Production Deployment

For production environments, you may want to manage database migrations separately from your application. Alberto
provides a migration script generator tool that extracts SQL scripts for manual review and deployment.

### Generating Migration Scripts

```bash
# First, build your application
dotnet build YourApp.csproj -c Release

# Navigate to the script generator
cd src/Tools/Alberto.MigrationScriptGenerator

# Generate scripts (including projection tables from your app)
dotnet run -- \
  --assembly ../../../YourApp/bin/Release/net10.0/YourApp.dll \
  --schemas orders,payments \
  --output ./migrations

# Or generate just EventStore and Projection schemas (without projection tables)
dotnet run -- --schemas orders,payments --output ./migrations

# Review generated scripts
ls ./migrations/
# 001_eventstore_orders.sql
# 001_eventstore_payments.sql
# 001_projections_schema_orders.sql
# 001_projections_schema_payments.sql
# 002_projections_table_orders_ordersummary.sql  (if --assembly provided)
# 002_projections_table_orders_customerview.sql  (if --assembly provided)
```

**How it works:**

- The tool scans your assembly for types implementing `IProjector<TState>`
- For each `TState` found, it generates a CREATE TABLE script
- Scripts match the structure created by `PostgresProjectionRepository.InitializeTable()`
- If no assembly is provided, projection tables will be auto-created at runtime (backward compatible)

### Deploying to Production

Use the generated SQL scripts with your preferred deployment tool:

```bash
# Using psql
psql -h prod-db.example.com -U admin -d mydb -f migrations/001_eventstore_orders.sql

# Using Azure CLI
az postgres flexible-server execute --name myserver --database mydb --file-path migrations/001_eventstore_orders.sql

# Or integrate with your CI/CD pipeline (Flyway, Liquibase, etc.)
```

**Note**: The generated scripts are idempotent (use `CREATE IF NOT EXISTS`) and can be run multiple times safely.

## Architecture

Alberto uses a **multi-backend architecture** with a factory pattern (`IEventStoreBackendFactory`) to abstract between
different storage implementations. The main `EventStore` class delegates to backend implementations through
`IEventStoreBackend`.

### Key Components

- **EventStore**: Main event store facade with dependency injection integration
- **EventStore.InMemory**: In-memory implementation for testing and development
- **EventStore.Postgres**: PostgreSQL-based production implementation with schema isolation
- **EventStore.Telemetry**: Diagnostics and telemetry integration

### Multi-Schema Support

The PostgreSQL implementation supports multiple schemas within the same database for logical separation (e.g., "
orders", "payments"). Each schema is configured via `AddPostgresEventStore(schemaName, options)` and uses
`ISchemaContext` to determine which backend instance to use.

## Features

- **Multi-Backend Architecture**: In-memory and PostgreSQL implementations with factory pattern
- **Multi-Tenant Support**: Isolated event streams per tenant with `ITenantContext`
- **Multi-Schema Support**: Schema-based logical separation in PostgreSQL for different domains
- **Optimistic Concurrency**: Consistency boundaries with expected event IDs
- **High Performance**: Optimized bulk operations and connection pooling
- **Stream Queries**: Advanced filtering by event types, tags with boolean operators
- **Aspire Integration**: .NET Aspire orchestration with automatic dependency management
- **Docker Integration**: Full Testcontainers support for testing

## Testing

The project uses a two-tier testing approach:

### Fast Feedback Tests (`EventStore.Tests`)

- **109 tests** running in ~3 seconds
- Unit and integration tests for correctness
- Runs on every push/PR for immediate feedback
- Command: `dotnet test` (excludes performance tests for fast feedback)

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

See [Performance Tests README](tst/Alberto.EventStore.Performance.Tests/README.md) for detailed benchmarks.

## CI/CD

- **Main Pipeline**: Complete build, test, and publish pipeline with GitVersion integration (~2-3 minutes)
    - Triggers: Push to main/develop, tags matching `v*.*.*`, PRs to main/develop
    - Publishing: Release packages to NuGet.org for tags and main branch, beta pre-releases for develop
- **Performance Pipeline**: Performance regression detection and optimization (10-30 minutes)
    - Triggers: Manual dispatch, releases, weekly schedule (Monday 6 AM UTC)