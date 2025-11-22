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

#### Event Store Layer
- **EventStore**: Main event store facade with dependency injection integration and ModuleBuilder pattern
- **EventStore.InMemory**: In-memory implementation for testing and development
- **EventStore.Postgres**: PostgreSQL-based production implementation with:
    - Schema isolation and connection pooling
    - JSONB event storage with covering indexes
    - Bulk insert optimization (threshold: 5 events)
    - Automatic migrations and schema management
    - Subscription infrastructure (checkpoints, poison pills, distributed locking)
- **EventStore.Telemetry**: OpenTelemetry integration for distributed tracing and diagnostics

#### Event Sourcing Layer

- **EventSourcing**: Minimal event sourcing building blocks:
    - `IProjector<TState>`: Event projection interface
    - `Load/Persist` helpers with optimistic concurrency
    - **Snapshots**: State snapshot infrastructure for performance optimization
        - `ISnapshotStore<TKey, TState>`: Snapshot storage abstraction
        - `InMemorySnapshotStore`: In-memory snapshot implementation
        - Configurable snapshot strategies (event count, time-based, never)
    - **Event Versioning**: Schema evolution support
        - `IEventUpcaster`: Event transformation interface
        - `EventUpcasterRegistry`: Automatic upcasting during deserialization
        - `EventVersionAttribute`: Version tracking
- **Projections.InMemory**: In-memory projection repositories for testing
- **Projections.Postgres**: PostgreSQL-based projection storage with:
    - JSONB state storage for schema flexibility
    - Automatic table creation per projection type
    - Batch updates with version checking
    - Global version tracking for idempotency

#### CQRS Layer (Optional)

- **CQRS**: Full-featured CQRS framework with:
    - Commands, queries, and handlers
    - Result/Problem/Decision pattern for functional error handling
    - FluentValidation integration
    - Assembly scanning for auto-registration
    - Module isolation via keyed services
- **CQRS.Telemetry**: OpenTelemetry integration for command/query tracing

#### Testing Infrastructure

- **ComponentTests**: Integration testing framework with:
    - UseCase pattern for Arrange-Act-Assert workflows
    - Step-based test organization
    - WebApplicationFactory integration
- **UnitTests**: Specification pattern for:
    - Stateless decision testing
    - Stateful aggregate testing with projectors
    - xUnit v3 integration

#### Example Application

- **Example**: .NET Aspire-based reference application with:
    - Orders and Payments bounded contexts
    - Multi-module architecture with schema isolation
    - Hybrid subscriptions (sync + async)
    - Full telemetry integration
    - Load testing with k6

### Key Patterns

**Module-Based Architecture**: Each EventStore is a module identified by a unique `TEventStore` type, enabling multiple
isolated event stores with different backends and configurations in the same application. Configured via
`ModuleBuilder<TEventStore>` fluent API.

**Multi-Tenant Support**: Events are isolated by tenant using `ITenantContext`. The PostgreSQL implementation supports
multiple schemas for tenant isolation.

**Stream Queries**: Events are queried using `StreamQuery` objects with builder pattern that can filter by:

- Event types with wildcard support
- Tags (domain identifiers) with boolean operators (ALL vs ANY)
- Position-based streaming
- Consistency boundaries for optimistic concurrency

**Subscription System**: Supports both polling and channel-based subscriptions with three modes:

- **Sync**: Runs inline with append (strong consistency)
- **Async**: Runs in background (high throughput)
- **Hybrid**: Mix of sync and async (recommended)

**Subscription Resilience**: Production-ready error handling with:

- **Circuit Breaker**: Prevents cascading failures by opening circuit after repeated failures
- **Dead Letter Queue**: Captures events that fail after all retries for investigation
- **Poison Pill Store**: Persistent tracking of problematic events
- **Retry Policies**: Configurable retry count and exponential backoff
- **Health Monitoring**: Circuit breaker state tracking for observability

**Event Metadata Enrichment**: Automatic context tracking with built-in enrichers:

- **CorrelationIdEnricher**: Groups related operations across aggregates (uses TraceId from OpenTelemetry)
- **CausationIdEnricher**: Tracks direct cause-and-effect relationships between events
- **UserContextEnricher**: Captures user information (ID, name, email, roles, IP) for audit trails
- **SourceEnricher**: Records service name, version, environment, and machine name
- All metadata queryable and used for distributed tracing

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

#### Event Store Core
- `EventStore/EventStoreFactory.cs`: Base factory class for all event stores
- `EventStore/ModuleBuilder.cs`: Fluent configuration API for modules
- `EventStore/IEventStoreBackend.cs`: Backend abstraction interface
- `EventStore/Versioning/IEventUpcaster.cs`: Event schema evolution interface
- `EventStore/Versioning/EventUpcasterRegistry.cs`: Automatic upcasting during deserialization
- `EventStore/Metadata/IEventMetadataEnricher.cs`: Metadata enrichment interface
- `EventStore/Metadata/CorrelationIdEnricher.cs`: Correlation ID tracking
- `EventStore/Metadata/CausationIdEnricher.cs`: Causation ID tracking
- `EventStore/Metadata/UserContextEnricher.cs`: User audit trail
- `EventStore/Metadata/SourceEnricher.cs`: Service source tracking

#### Backends
- `EventStore.InMemory/InMemoryEventStoreBackend.cs`: Full in-memory implementation
- `EventStore.Postgres/PostgresEventStoreBackend.cs`: PostgreSQL backend with JSONB storage
- `EventStore.Postgres/PostgresModuleBuilderExtensions.cs`: `.WithPostgres()` extension
- `EventStore.Postgres/Migrations/MigrationHostedService.cs`: Automatic schema migrations

#### Subscriptions

- `EventStore/Subscriptions/Channel/ChannelSubscriptionRegistry.cs`: In-process pub/sub
- `EventStore/Subscriptions/Resilience/CircuitBreaker.cs`: Circuit breaker pattern
- `EventStore/Subscriptions/Resilience/DeadLetterQueue.cs`: Failed event tracking
- `EventStore.Postgres/Subscriptions/Checkpoints/PostgresCheckpointStore.cs`: Subscription positions
- `EventStore.Postgres/Subscriptions/PoisonPills/PostgresPoisonPillStore.cs`: Poison pill tracking
- `EventStore.Postgres/Subscriptions/DistributedLocking/PostgresAdvisoryLock.cs`: Distributed locking

#### Event Sourcing

- `EventSourcing/Projectors/IProjector.cs`: Event projection interface
- `EventSourcing/EventStoreExtensions.cs`: Load/Persist helpers for EventStore
- `EventSourcing/Snapshots/ISnapshotStore.cs`: Snapshot storage abstraction
- `EventSourcing/Snapshots/SnapshotStrategy.cs`: Snapshot triggering strategies
- `EventSourcing/Snapshots/SnapshotExtensions.cs`: Load/Persist with snapshot optimization

#### Projections

- `Projections.Postgres/PostgresProjectionRepository.cs`: JSONB-based projection storage with batch updates
- `Projections.Postgres/PostgresProjectionBuilderExtensions.cs`: `.AddPostgresProjection()` extension
- `EventSourcing/Projections/IProjectionRepository.cs`: Projection repository interface with batch operations

#### CQRS
- `CQRS/Commands/CommandExecutor.cs`: Command execution with validation
- `CQRS/Queries/QueryExecutor.cs`: Query execution with validation
- `CQRS/Results/Result.cs`: Functional result types
- `CQRS/Results/Decision.cs`: Decision pattern for domain logic
- `CQRS/Results/Problem.cs`: Error representation
- `CQRS/ModuleBuilderExtensions.cs`: `.WithCQRS()` extension for auto-registration

#### Telemetry

- `EventStore.Telemetry/ActivityDiagnosticEventListener.cs`: OpenTelemetry integration
- `CQRS.Telemetry/ActivityDiagnosticEventListener.cs`: Command/query tracing

#### Testing
- `ComponentTests/UseCase.cs`: Component test framework fluent API
- `UnitTests/Specification.cs`: Unit test specification pattern
- `ComponentTests/ScenarioContext.cs`: Test context and state management
- `UnitTests/Specification.cs`: Unit test specification pattern for stateless commands
- `UnitTests/Specification<TState>.cs`: Unit test specification pattern with projectors
- `Example/Modules/Orders/OrdersModule.cs`: Orders module registration
- `Example/Modules/Orders/OrderProblems.cs`: Centralized error definitions
- `Example/Modules/Orders/OrderDecisions.cs`: Pure business logic
- `Example/AppHost/AppHost.cs`: Aspire orchestration setup

See project-specific CLAUDE.md files in each package for detailed architecture documentation.

### Event Sourcing & CQRS Layers

**Alberto.EventSourcing** (Minimal building blocks):

- `IProjector<TState>` - Projects events into state
- `ProjectorExtensions.Evolve()` - Folds events into state
- `EventStoreExtensions` - Load/Persist helpers with optimistic concurrency

**Alberto.CQRS** (Opt-in opinionated framework):

- Commands, Queries, Handlers - CQRS pattern implementation
- Result/Problem/Decision - Functional error handling
- Validation pipeline - FluentValidation integration
- Auto-registration - Assembly scanning for handlers/validators
- Module isolation - Keyed services per EventStore type

Users can:

- Use only EventSourcing for minimal event sourcing
- Add CQRS for full framework with validation and auto-registration
- Use their own CQRS framework (MediatR, Wolverine) with EventSourcing

See `EventSourcing/README.md` and `CQRS/README.md` for detailed usage.

## Advanced Features

### Event Versioning & Upcasting

Alberto supports event schema evolution through upcasters:

```csharp
// Define event versions
[EventVersion("1")]
public record OrderCreatedV1(Guid OrderId, string CustomerId, decimal Amount);

[EventVersion("2")]
public record OrderCreatedV2(Guid OrderId, string BuyerId, decimal Amount, string Currency);

// Create upcaster
public class OrderCreatedUpcaster : IEventUpcaster
{
    public string FromEventType => "OrderCreated";
    public string FromVersion => "1";
    public string ToVersion => "2";

    public string Upcast(string eventJson, IReadOnlyDictionary<string, string> metadata)
    {
        var v1 = JsonSerializer.Deserialize<OrderCreatedV1>(eventJson);
        var v2 = new OrderCreatedV2(
            v1.OrderId,
            v1.CustomerId,  // Renamed field
            v1.Amount,
            "USD"           // New field with default
        );
        return JsonSerializer.Serialize(v2);
    }
}

// Register upcaster
var registry = new EventUpcasterRegistry();
registry.Register(new OrderCreatedUpcaster());
```

### Snapshots for Performance

Optimize aggregate hydration with snapshots:

```csharp
// Configure snapshot strategy
var snapshotStrategy = new EventCountSnapshotStrategy(eventThreshold: 100);
var snapshotStore = new InMemorySnapshotStore<Guid, OrderState>();

// Load with snapshot optimization
var (state, lastEventId, snapshotInfo) = await eventStore.LoadWithSnapshot(
    snapshotStore,
    projector,
    orderId,
    query,
    cancellationToken);

// Persist with automatic snapshot creation
await eventStore.PersistWithSnapshot(
    snapshotStore,
    snapshotStrategy,
    orderId,
    newState,
    query,
    lastEventId,
    events,
    snapshotInfo?.Position,
    snapshotInfo?.EventsLoadedAfterSnapshot ?? 0,
    cancellationToken);
```

**Performance Impact:** 10-100x faster for aggregates with 1000+ events.

### Event Metadata Tracking

Enrich events with contextual metadata:

```csharp
// Set correlation and causation IDs
CorrelationIdEnricher.SetCorrelationId("order-workflow-123");
CausationIdEnricher.SetCausationId(previousEventId);

// Set user context
UserContextEnricher.SetUserContext(new UserContext(
    UserId: "user-123",
    UserName: "john.doe",
    Email: "john@example.com",
    Roles: new List<string> { "Admin" },
    IpAddress: "192.168.1.1"
));

// Events will automatically include:
// - correlation_id: Groups related operations
// - causation_id: Direct cause-and-effect
// - user_id, user_name, user_email, user_roles, user_ip
// - source_service, source_version, source_environment
// - traceparent (OpenTelemetry trace context)
```

### Subscription Resilience

Production-ready error handling:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => { /* ... */ })
    .WithChannelSubscriptions(channel => channel
        .ConfigureAsync(options =>
        {
            // Circuit breaker
            options.CircuitBreaker = new CircuitBreakerOptions
            {
                Enabled = true,
                FailureThreshold = 10,      // Open after 10 failures
                ResetTimeout = TimeSpan.FromSeconds(60)
            };

            // Dead letter queue
            options.DeadLetterQueue = new DeadLetterQueueOptions
            {
                Enabled = true,
                MaxRetries = 5
            };

            // Retry policy
            options.MaxRetries = 5;
            options.RetryDelayMs = 250;
        })
        .AddPostgresProjection<OrderEventStore, OrderProjection, Guid, Order, OrderProjector>(
            mode: SubscriptionMode.Hybrid)));
```

### Batch Projection Updates

Optimize high-throughput projections:

```csharp
// Batch update multiple projections in single transaction
var updates = new Dictionary<Guid, (Order State, long Version)>
{
    [order1Id] = (order1State, position1),
    [order2Id] = (order2State, position2),
    [order3Id] = (order3State, position3)
};

var rowsAffected = await repository.BatchUpsertWithVersion(updates, cancellationToken);
```

**Performance Impact:** 5-10x faster for bulk updates.

## Project Structure

The solution uses solution folders to organize projects:

- **EventStore folder**: Core event store components
  - `EventStore` - Core abstractions and ModuleBuilder
  - `EventStore.InMemory` - In-memory backend
  - `EventStore.Postgres` - PostgreSQL backend with connection pooling
  - `EventStore.Telemetry` - OpenTelemetry integration
  - `EventStore.Tests` - Unit and integration tests
  - `EventStore.Performance.Tests` - BenchmarkDotNet performance tests

- **EventSourcing folder**: Event sourcing and projections
  - `EventSourcing` - Minimal building blocks (IProjector, Load/Persist)
  - `Projections.InMemory` - In-memory projection repositories
  - `Projections.Postgres` - PostgreSQL projection storage with JSONB

- **CQRS folder**: Optional CQRS framework
  - `CQRS` - Commands, queries, handlers, validation, auto-registration
  - `CQRS.Telemetry` - OpenTelemetry integration for CQRS

- **Testing folder**: Reusable testing frameworks
  - `ComponentTests` - Integration test utilities (UseCase pattern)
  - `UnitTests` - Specification pattern for unit tests

- **Example folder**: Aspire-based example application
  - `Alberto.Example` - Orders and Payments modules
  - `AppHost` - Aspire orchestration
  - `ServiceDefaults` - Shared Aspire configuration

- **Test Projects**: Tests for example application
  - `Alberto.Example.UnitTests` - Business logic tests
  - `Alberto.Example.ComponentTests` - Full stack integration tests
  - `Alberto.Example.LoadTests` - k6-based load tests

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

### Unit Tests (`Example.UnitTests`)

- **Purpose**: Fast, isolated testing of business logic (deciders/projectors) without infrastructure
- **Test count**: 15 tests running in ~100ms
- **Coverage**:
  - Decision logic for all commands (CreateOrder, PlaceOrder, ShipOrder, CancelOrder)
  - Success scenarios (valid state transitions)
  - Failure scenarios (business rule violations, invalid states)
  - All problem/error cases defined in `OrderProblems.cs`
- **Framework**: Custom specification pattern (`Specification<TState>`) built on xUnit v3
- **Test Pattern**:
  - `new Specification<TState>(projector)` - Creates specification with projector
  - `.Given(events...)` - Sets up initial state from events
  - `.When(state => decider.Decide(...))` - Executes decision logic
  - `.ThenEventOfType<T>()` / `.ThenFailWith(problem)` - Verifies outcome
- **Key Files**:
  - `Specification.cs` - Stateless specification for commands without history
  - `Specification<TState>.cs` - Stateful specification with projectors
  - `SpecificationBase.cs` - Base class with assertion helpers
  - Tests in `Orders/` folder (e.g., `CancelOrder_Should.cs`, `PlaceOrder_Should.cs`)
- **Technology**: xUnit v3
- **Scope**: Pure business logic, no HTTP/database/infrastructure

### Component Tests (`Example.ComponentTests`)

- **Purpose**: Integration testing of full request pipeline (HTTP → validation → handlers → event store)
- **Test count**: 10 tests running in ~600ms
- **Coverage**:
  - Happy path integration for each command (verifies full stack works)
  - FluentValidation rules (INVALID_AMOUNT, INVALID_CUSTOMER, INVALID_REASON, INVALID_TRACKING_NUMBER)
  - API-level concerns (serialization, routing, HTTP status codes)
  - Complex workflows (Create → Place → Ship)
- **Framework**: Custom component testing framework (`Alberto.ComponentTests`) built on xUnit v3
- **Test Organization**:
  - Tests organized by feature in `Orders/Features/` folder (e.g., `CancelOrderTests.cs`, `CreateOrderTests.cs`)
  - Steps (actions and assertions) in `Orders/Steps/Orders/` folder
  - Fixtures provide test context and service configuration
- **Test Pattern**:
  - `UseCase()` - Creates test scenario
  - `.Arrange(steps...)` - Setup actions (no verification in arrange phase)
  - `.Act(steps...)` - Action under test
  - `.Assert(steps...)` - Verify outcomes
- **Key Components**:
  - `IStep` - Interface for all test steps (actions and assertions)
  - `ScenarioContext` - Manages test state and provides access to HttpClient and services
  - `UseCase` - Fluent API for building test scenarios
  - Action steps (e.g., `CreateOrder`, `CancelOrder`, `PlaceOrderStep`, `ShipOrderStep`)
  - Assertion steps (e.g., `HttpSuccessResponse`, `HttpFailureResponse`, `OrderCreated`, `OrderIsPlaced`,
    `OrderIsShipped`, `OrderIsCancelled`)
- **Technology**: xUnit v3, Microsoft.AspNetCore.Mvc.Testing for WebApplicationFactory integration
- **Scope**: Full integration, infrastructure included

### Testing Philosophy: Unit vs Component

**Unit tests** cover business logic edge cases (decider decisions, state transitions, business rules). These are fast,
deterministic, and test the domain logic in isolation.

**Component tests** verify the full stack integration (HTTP → validation → command handling → event persistence). These
focus on happy paths and validation rules, avoiding duplication of business logic already covered by unit tests.

**Why this split?**

- Avoid testing the same business logic twice (once at unit level, once through HTTP)
- Fast feedback loop: unit tests run in milliseconds, component tests take longer due to infrastructure
- Clear separation: unit tests = pure logic, component tests = integration & API concerns
- Better maintainability: business rule changes only require updating unit tests, not full integration tests

### Test Architecture Patterns

- **Specification Pattern**: `EventStoreBackendSpecification.cs` defines abstract test contracts
- **Backend Implementations**: Each backend (InMemory, Postgres) implements the specification
- **Advanced Query Tests**: `AdvancedQueryTests.cs` provides complex scenario testing
- **Fixture-based Setup**: `PostgresTestFixture` manages database lifecycle and tenant isolation
- **Component Test Pattern**: Feature-based tests using Arrange-Act-Assert with reusable step framework

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