# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

- **Build solution**: `dotnet build Alberto.sln`
- **Run tests**: `dotnet test`
- **Run specific test project**: `dotnet test Eventstore.Tests/Eventstore.Tests.csproj`
- **Run example application**: `dotnet run --project Example/Alberto.AppHost/Alberto.AppHost.csproj`
- **Start individual services**:
  - Example web app: `dotnet run --project Example/Alberto.Example/Alberto.Example.csproj`
  - SQL migrator: `dotnet run --project Example/Alberto.SqlMigrator/Alberto.SqlMigrator.csproj`

## Architecture Overview

Alberto is an event store library for .NET with multi-tenant and multi-schema support, built on .NET 10.

### Core Components

- **EventStore**: Main event store facade with dependency injection integration
- **EventStore.InMemory**: In-memory implementation for testing and development
- **EventStore.Postgres**: PostgreSQL-based production implementation with schema isolation
- **EventStore.Telemetry**: Diagnostics and telemetry integration
- **Example**: .NET Aspire-based example application demonstrating usage

### Key Patterns

**Multi-Backend Architecture**: The system uses a factory pattern (`IEventStoreBackendFactory`) to abstract between different storage implementations. The main `EventStore` class delegates to backend implementations through `IEventStoreBackend`.

**Multi-Tenant Support**: Events are isolated by tenant using `ITenantContext`. The PostgreSQL implementation supports multiple schemas for tenant isolation.

**Stream Queries**: Events are queried using `StreamQuery` objects that can filter by:
- Event types with wildcard support
- Tags (domain identifiers) with boolean operators (ALL vs ANY)
- Consistency boundaries for optimistic concurrency

**Optimistic Concurrency**: Append operations support consistency boundaries with expected last event IDs to prevent conflicts.

**Aspire Integration**: The example uses .NET Aspire for orchestration with PostgreSQL and automatic dependency management.

### Multi-Schema Support

The PostgreSQL implementation supports multiple schemas within the same database:
- Each schema represents a logical boundary (e.g., "orders", "payments")
- Configured via `AddPostgresEventStore(schemaName, options)`
- Schema context (`ISchemaContext`) determines which backend instance to use

### Key Files

- `EventStore/EventStore.cs`: Main event store facade
- `EventStore/IEventStoreBackend.cs`: Backend abstraction interface
- `EventStore.InMemory/InMemoryEventStoreBackend.cs`: Full in-memory implementation
- `EventStore.Postgres/PostgresEventStoreBackendFactory.cs`: PostgreSQL factory using keyed services
- `Example/Alberto.Example/Program.cs`: Example service configuration
- `Example/Alberto.AppHost/AppHost.cs`: Aspire orchestration setup

## Project Structure

The solution uses solution folders to organize projects:
- **EventStore folder**: Core event store components (`EventStore`, `EventStore.InMemory`, `EventStore.Postgres`, `EventStore.Telemetry`, `Eventstore.Tests`)
- **Example folder**: Aspire-based example application (`Alberto.Example`, `Alberto.AppHost`, `Alberto.ServiceDefaults`, `Alberto.SqlMigrator`)

## Dependencies and Technology

- .NET 10 with nullable reference types and implicit usings enabled
- xUnit v3 for testing with Microsoft.NET.Test.Sdk
- Testcontainers for integration testing
- .NET Aspire for orchestration and service defaults
- PostgreSQL with schema-based multi-tenancy
- Microsoft.Extensions.* for dependency injection and configuration