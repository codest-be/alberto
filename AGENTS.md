# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

Alberto is a DCB (Dynamic Consistency Boundary) Event Store for .NET 10.0. The repository is a monorepo containing packable core libraries (`/src`), example applications (`/apps`), an operator CLI (`/tools`), and tests (`/tests`).

There is no frontend in this repository — the event store is a library plus a terminal CLI.

## Build & Run Commands

### Full Stack (Recommended)
```bash
dotnet run --project apps/Alberto.AppHost
```
Aspire starts PostgreSQL, runs the Orders migrations, then starts the Orders API. K6 load tests are registered as a manually-triggered resource in the Aspire dashboard.

### .NET Backend
```bash
dotnet build                    # Build all projects
dotnet test                     # Run all xUnit tests
dotnet test --filter "FullyQualifiedName~TestName"  # Run single test
```

Postgres-backed tests use Testcontainers and need a running Docker daemon.

### Load Tests
```bash
cd tests/Alberto.Orders.LoadTests
npm install && npm run build
npm run test:smoke              # Quick validation
npm run test:load               # Full load test
```

### Operator CLI
```bash
dotnet run --project tools/Alberto.Cli -- status
```

### Benchmarks
```bash
dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --filter '*Append*'
```

## Architecture

### Directory Structure
```
/src/                           # Core libraries (packable NuGet)
  Alberto.Dcb/                  # Event store abstractions, control loop, middleware
  Alberto.Dcb.Commands/         # Command handling
  Alberto.Dcb.InMemory/         # In-memory backend (dev/test)
  Alberto.Dcb.Postgres/         # PostgreSQL backend, migrations, admin data access
  Alberto.Dcb.EntityFramework/  # EF-backed projections
  Alberto.Dcb.Messaging/        # Transactional outbox abstractions
  Alberto.Dcb.Postgres.Messaging/  # PostgreSQL outbox store
  Alberto.Dcb.Telemetry/        # OpenTelemetry instrumentation

/apps/                          # Example applications
  Alberto.AppHost/              # Aspire orchestration
  Alberto.Examples.Shared/      # MutationResult and the Result→GraphQL error mapping
  ServiceDefaults/              # Shared Aspire service configuration
  Alberto.Orders/               # Orders example
    Alberto.Orders/             # The module: Contracts/, Features/ (one folder per slice), Platform/
    Alberto.Orders.Api/         # Host: Program.cs, tenant interceptor
    Alberto.Orders.Migrations/  # EF migrations runner
  Alberto.Payments/             # Payments example
    Alberto.Payments/           # The module, same shape as Orders

/tools/
  Alberto.Cli/                  # Operator CLI (Spectre.Console + System.CommandLine)

/tests/
  Alberto.Dcb.Tests/            # Unit + Testcontainers integration tests (xUnit 3)
  Alberto.Orders.LoadTests/     # K6 load tests (TypeScript)

/benchmarks/
  Alberto.Dcb.Benchmarks/       # BenchmarkDotNet append/read/checkpoint benchmarks
```

Note: `apps/Alberto.Payments` is in the solution and builds, but it has no host of its own and is not orchestrated by the AppHost — its slices are registered by the Orders API host, which serves their GraphQL fields alongside Orders'.

### Key Patterns
- **Event Sourcing with DCB**: Append-only event log with dynamic consistency boundaries
- **Vertical slices in the examples**: `apps/Alberto.Orders` and `apps/Alberto.Payments` are sliced by behaviour, not layer. One folder per slice under `Features/`, holding that slice's input type, state record, evolver, decision function, boundary and GraphQL operation. **Slices share the event log and nothing else** — no shared state record, no shared evolver, no base state. `Contracts/` (events, status enums, problem codes, tag keys) and `Platform/` (DI, `DbContext`, EF migrations) are the two deliberate exceptions, named so they cannot be mistaken for domain code that happens to be shared. Five slices fold `OrderCreated`, each projecting a different part of it; that duplication is the pattern working. See [docs/architecture/vertical-slices.md](docs/architecture/vertical-slices.md)
- **Async Processing**: `ControlLoop` polls the event log and dispatches through a middleware chain to projections/reactors. See [docs/architecture/async-processing.md](docs/architecture/async-processing.md)
- **Middleware**: `MiddlewareRunner` builds both the single-event (`ConsumeEventContext`) and batch (`BatchConsumeContext`) chains. Retry/dead-letter logic is shared via `RetryAndDeadLetterCore` behind `IMiddlewareContext`
- **Zero-downtime projection rebuilds**: opt in with `.WithRebuilds()`. `RebuildCoordinator` replays the log into a shadow copy of a projection's state under its own checkpoint, then swaps versions in one transaction. Driven by `alberto ops rebuild start|status|promote|abort`
- **Multi-Tenant**: X-Tenant-Id header propagation, tenant-isolated queries, tenant leases
- **Projection tenancy**: a state store's tenancy is fixed when the store is built and decided by the schema, not by the caller — a module that declared `.WithTenancy()` is migrated with `tenant_id NOT NULL` in its primary key, so a store built without one fails every write with `42P10`, and the reverse mismatch fails with `42703`. `AddProjection` therefore takes a `Func<string?, IStateStore<TState>>`: the projection builds one store per tenant and routes each event to the store for the tenant it carries. A cross-tenant aggregate on a tenant-enabled module stores its single blended document under `TenantScope.CrossTenant` (`"*"`), which readers pass too — resolvers resolve the writer's own factory from DI (`{moduleKey}:{processorId}`) so the only thing they decide is which tenant to read
- **Leases and fencing**: checkpoint writes can be fenced against a held lease via `IFencedCheckpointStore`
- **Transactional outbox**: `IOutboxStore` with `pending → processing → delivered/failed`, claimed via `FOR UPDATE SKIP LOCKED` under a claim lease (`claim_id`, `claimed_by`, `claim_expires_at`). A relay that dies mid-delivery does not strand its row: `ClaimPendingAsync` re-claims any `processing` entry whose `claim_expires_at` has passed or was never set. `RetryFailedAsync` is a separate operator action and matches `failed` only
- **GraphQL** (Orders example only): HotChocolate 15.x

### Admin surface
The operator surface is the CLI in `tools/Alberto.Cli`. There is no admin HTTP API. `src/Alberto.Dcb.Admin` contains the `IAdminReader`/`IAdminOperator` abstraction the CLI's command files are built on; it serves no endpoint.

**Both admin projects are `IsPackable=false`.** `Alberto.Dcb.Admin` and `Alberto.Dcb.Postgres.Admin` build, sit in the solution, are tested, and are referenced by the CLI as projects — they just do not ship to nuget.org, so 1.0 does not freeze `IAdminReader`/`IAdminOperator` under semver before the front doors on `feature/admin-surface` exist. `Alberto.Dcb.Postgres.Admin` was split out of `Alberto.Dcb.Postgres` (which **is** packable) precisely so that package carries no dependency on a parked one; its files keep `namespace Alberto.Dcb.Postgres`, so no consumer's usings changed.

- **Per-processor mutations** go through the core interfaces: `ICheckpointStore` (`SaveAsync`, `ResetAsync`, `RewindAsync`) and `IDeadLetterStore` (`CountAsync`, `ClearAsync`, `MarkForRetryAsync`).
- **`PostgresAdminDataAccess`** (`src/Alberto.Dcb.Postgres.Admin`) holds the inspection queries and the composite transactional mutations (`RetryByRewindAsync`, `ReleaseTenantLeasesAsync`) that span multiple tables and so cannot be composed from per-processor interfaces.
- `SaveAsync` is monotonic by design (`GREATEST`). `RewindAsync` is the deliberate escape hatch for operator-initiated rewinds and is the only way to move a checkpoint backwards.

## Technology Stack

| Layer | Technology |
|-------|------------|
| Framework | .NET 10.0, ASP.NET Core |
| GraphQL (Orders example) | HotChocolate 15.1.15 |
| Database | PostgreSQL (Npgsql 10.0.2) |
| Migrations | DbUp-PostgreSQL 7.0.1 (event store), EF Core 10.0.7 (Orders) |
| Observability | OpenTelemetry 1.15.3 |
| Testing | xUnit v3 3.2.2, FluentAssertions 8.9.0, Testcontainers 4.11.0 |
| CLI | Spectre.Console 0.55.2, System.CommandLine 2.0.7 |
| Load Testing | K6 with TypeScript |

## Package Management

- **NuGet**: Centralized versions in `Directory.Packages.props`
- **npm**: Per-project `package.json` files

## Configuration

- **Solution file**: `Alberto.slnx` (modern .NET format)
- **Build settings**: `Directory.Build.props`

## Known Gaps

Documented so they are not mistaken for working features:

- **Promotion opens a one-query window where a reader sees nothing.** A reader resolves the active version and then queries it; promotion deletes the superseded rows in the same transaction that flips the version, so a promotion landing between those two steps leaves the reader holding a version number that no longer exists. Closing it properly means deferring the delete until every reader has refreshed its version cache — a grace period tied to `ProjectionVersions`' refresh interval — rather than deleting inside the flip. Reproduced under five concurrent test processes (`Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection`, ~1 run in 20); not reproducible on an unloaded machine.
- **An aborted version's rows can outlive the abort by a tick.** `AbortedRebuild_LeavesTheLiveVersionUntouched` occasionally finds state still present at the abandoned version: the abort transaction deletes it, but the shadow loop only learns of the abort on its next poll and its late writes land afterwards. The sweep clears them on a later tick, so this is a lag rather than a leak — but a test asserting emptiness immediately after an abort is asserting something the design does not promise. Same frequency and conditions as above.
