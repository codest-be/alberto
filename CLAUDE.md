# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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
  ServiceDefaults/              # Shared Aspire service configuration
  Alberto.Orders/               # Orders example
    Alberto.Orders.Api/         # GraphQL API
    Alberto.Orders.Core/        # Domain models
    Alberto.Orders.Infrastructure/  # Data access
    Alberto.Orders.Migrations/  # EF migrations runner
  Alberto.Payments/             # Payments example (Core + Infrastructure only)

/tools/
  Alberto.Cli/                  # Operator CLI (Spectre.Console + System.CommandLine)

/tests/
  Alberto.Dcb.Tests/            # Unit + Testcontainers integration tests (xUnit 3)
  Alberto.Orders.LoadTests/     # K6 load tests (TypeScript)

/benchmarks/
  Alberto.Dcb.Benchmarks/       # BenchmarkDotNet suites (not in AlbertoV3.slnx)
```

Note: `apps/Alberto.Payments` is in the solution and builds, but it is not orchestrated by the AppHost — its projections and read models are consumed by the Orders API.

### Key Patterns
- **Event Sourcing with DCB**: Append-only event log with dynamic consistency boundaries
- **Async Processing**: `ControlLoop` polls the event log and dispatches through a middleware chain to projections/reactors. See [docs/architecture/async-processing.md](docs/architecture/async-processing.md)
- **Middleware**: `MiddlewareRunner` builds both the single-event (`ConsumeEventContext`) and batch (`BatchConsumeContext`) chains. Retry/dead-letter logic is shared via `RetryAndDeadLetterCore` behind `IMiddlewareContext`
- **Zero-downtime projection rebuilds**: opt in with `.WithControlLoop(loop => loop.WithRebuilds())`. `RebuildCoordinator` replays the log into a shadow copy of a projection's state under its own checkpoint, then swaps versions in one transaction. Driven by `alberto ops rebuild start|status|promote|abort`
- **Multi-Tenant**: X-Tenant-Id header propagation, tenant-isolated queries, tenant leases
- **Leases and fencing**: checkpoint writes can be fenced against a held lease via `IFencedCheckpointStore`
- **Transactional outbox**: `IOutboxStore` with `pending → processing → delivered/failed`, claimed via `FOR UPDATE SKIP LOCKED`
- **GraphQL** (Orders example only): HotChocolate 15.x

### Admin surface
The operator surface is the CLI in `tools/Alberto.Cli`. There is no admin HTTP API or admin package.

- **Per-processor mutations** go through the core interfaces: `ICheckpointStore` (`SaveAsync`, `ResetAsync`, `RewindAsync`) and `IDeadLetterStore` (`CountAsync`, `ClearAsync`, `MarkForRetryAsync`).
- **`PostgresAdminDataAccess`** (`src/Alberto.Dcb.Postgres`) holds the inspection queries and the composite transactional mutations (`RetryByRewindAsync`, `ReleaseTenantLeasesAsync`) that span multiple tables and so cannot be composed from per-processor interfaces.
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

- **Solution file**: `AlbertoV3.slnx` (modern .NET format)
- **Build settings**: `Directory.Build.props`

## Known Gaps

Documented so they are not mistaken for working features:

- **Promotion opens a one-query window where a reader sees nothing.** A reader resolves the active version and then queries it; promotion deletes the superseded rows in the same transaction that flips the version, so a promotion landing between those two steps leaves the reader holding a version number that no longer exists. Closing it properly means deferring the delete until every reader has refreshed its version cache — a grace period tied to `ProjectionVersions`' refresh interval — rather than deleting inside the flip. Reproduced under five concurrent test processes (`Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection`, ~1 run in 20); not reproducible on an unloaded machine.
- **An aborted version's rows can outlive the abort by a tick.** `AbortedRebuild_LeavesTheLiveVersionUntouched` occasionally finds state still present at the abandoned version: the abort transaction deletes it, but the shadow loop only learns of the abort on its next poll and its late writes land afterwards. The sweep clears them on a later tick, so this is a lag rather than a leak — but a test asserting emptiness immediately after an abort is asserting something the design does not promise. Same frequency and conditions as above.
- **Orphaned outbox entries have no reclaim path.** A relay that dies between claiming an entry (`processing`) and marking it `delivered`/`failed` strands the row. `alberto_outbox_entries` has no claim-lease columns, and `RetryFailedAsync` only matches `failed`. See the skipped test `DiscoveredIssuesTests.OutboxStore_ProcessingEntriesOrphaned_CannotBeRecoveredByRetryFailed`.
