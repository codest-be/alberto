# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

- **Build solution**: `dotnet build Alberto.sln`
- **Run tests**: `dotnet test` (excludes performance tests for fast feedback)
- **Run specific test project**: `dotnet test EventStore.Tests/EventStore.Tests.csproj`
- **Run performance tests**: `dotnet run --project EventStore.Performance.Tests --configuration Release`
- **Run example application**: `dotnet run --project Example/Alberto.AppHost/Alberto.AppHost.csproj`
- **Start individual services**:
    - Example web app: `dotnet run --project Example/Alberto.Example/Alberto.Example.csproj`
    - SQL migrator: `dotnet run --project Example/Alberto.SqlMigrator/Alberto.SqlMigrator.csproj`

## Versioning and Releases

The project uses **GitVersion** for automatic semantic versioning based on Git history and conventional commits:

- **Version calculation**: Automatic based on branch, commits, and tags
- **Main branch**: Produces release versions (e.g., `1.2.3`)
- **Develop branch**: Produces beta pre-releases (e.g., `1.2.3-beta.4`)
- **Feature branches**: Produces alpha pre-releases (e.g., `1.2.3-alpha.5`)
- **Tags**: Create releases by pushing tags like `v1.2.3`
- **Conventional commits**: Use `+semver: major/minor/patch` to control version bumps

## Architecture Overview

Alberto is an event store library for .NET with multi-tenant and multi-schema support, built on .NET 10.

### Core Components

- **EventStore**: Main event store facade with dependency injection integration
- **EventStore.InMemory**: In-memory implementation for testing and development
- **EventStore.Postgres**: PostgreSQL-based production implementation with schema isolation
- **EventStore.Telemetry**: Diagnostics and telemetry integration
- **Example**: .NET Aspire-based example application demonstrating usage

### Key Patterns

**Multi-Backend Architecture**: The system uses a factory pattern (`IEventStoreBackendFactory`) to abstract between
different storage implementations. The main `EventStore` class delegates to backend implementations through
`IEventStoreBackend`.

**Multi-Tenant Support**: Events are isolated by tenant using `ITenantContext`. The PostgreSQL implementation supports
multiple schemas for tenant isolation.

**Stream Queries**: Events are queried using `StreamQuery` objects that can filter by:

- Event types with wildcard support
- Tags (domain identifiers) with boolean operators (ALL vs ANY)
- Consistency boundaries for optimistic concurrency

**Optimistic Concurrency**: Append operations support consistency boundaries with expected last event IDs to prevent
conflicts.

**Aspire Integration**: The example uses .NET Aspire for orchestration with PostgreSQL and automatic dependency
management.

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

- **EventStore folder**: Core event store components (`EventStore`, `EventStore.InMemory`, `EventStore.Postgres`,
  `EventStore.Telemetry`, `EventStore.Tests`, `EventStore.Performance.Tests`)
- **Example folder**: Aspire-based example application (`Alberto.Example`, `Alberto.AppHost`, `Alberto.ServiceDefaults`,
  `Alberto.SqlMigrator`)

## Testing Strategy

The project uses a two-tier testing approach to separate fast feedback from comprehensive performance analysis:

### Unit and Integration Tests (`EventStore.Tests`)

- **Purpose**: Fast feedback for correctness and functionality
- **Test count**: 109 tests running in ~3 seconds
- **Coverage**:
    - Core event store functionality
    - Multi-schema isolation
    - Error handling and edge cases
    - PostgreSQL configuration validation
    - Concurrency correctness (small scale)
    - Multi-tenant isolation (small scale)
- **Technology**: xUnit v3 with Testcontainers for PostgreSQL integration
- **CI**: Runs on every push/PR for immediate feedback

### Performance Tests (`EventStore.Performance.Tests`)

- **Purpose**: Comprehensive performance analysis and regression detection
- **Test count**: 78 benchmarks covering various scenarios
- **Coverage**:
    - Single event operations
    - Bulk operations (10, 100, 1000 events)
    - Tag query performance
    - Connection pooling impact
    - Memory usage analysis
- **Technology**: BenchmarkDotNet with statistical analysis
- **CI**: Separate pipeline (manual, releases, weekly) to preserve GitHub Actions minutes

### Test Architecture Patterns

- **Specification Pattern**: `EventStoreBackendSpecification.cs` defines abstract test contracts
- **Backend Implementations**: Each backend (InMemory, Postgres) implements the specification
- **Advanced Query Tests**: `AdvancedQueryTests.cs` provides complex scenario testing
- **Fixture-based Setup**: `PostgresTestFixture` manages database lifecycle and tenant isolation

## CI/CD Pipelines

### Main Pipeline (`.github/workflows/build.yml`)

- **Triggers**: Push to main/develop branches, tags matching `v*.*.*`, PRs to main/develop, manual dispatch
- **Purpose**: Complete build, test, and publish pipeline with GitVersion integration
- **GitVersion**: Calculates versions automatically based on Git history
- **Tests**: Runs unit/integration tests across PostgreSQL versions (15, 16, 17)
- **Publishing**:
    - **Tags** (e.g., `v1.2.3`): Publishes release packages to NuGet.org
    - **Main branch**: Publishes release versions to NuGet.org
    - **Develop branch**: Publishes beta pre-releases to NuGet.org
    - **PRs**: Build and test only (no publishing)
- **Artifacts**: NuGet packages uploaded with 90-day retention
- **Duration**: ~2-3 minutes total

### Performance Pipeline (`.github/workflows/performance.yml`)

- **Triggers**: Manual dispatch, releases, weekly schedule (Monday 6 AM UTC)
- **Purpose**: Performance regression detection and optimization
- **Tests**: Runs comprehensive BenchmarkDotNet suite
- **Duration**: 10-30 minutes (varies by system load)
- **Artifacts**: Performance reports (JSON/HTML) retained for 30 days
- **Scope**: Performance analysis, regression detection, optimization guidance

## Dependencies and Technology

- .NET 10 with nullable reference types and implicit usings enabled
- xUnit v3 for testing with Microsoft.NET.Test.Sdk
- BenchmarkDotNet for performance testing
- Testcontainers for integration testing
- .NET Aspire for orchestration and service defaults
- PostgreSQL with schema-based multi-tenancy
- Microsoft.Extensions.* for dependency injection and configuration