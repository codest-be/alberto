# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Alberto is a DCB (Dynamic Consistency Boundary) Event Store for .NET 10.0. The repository is a monorepo containing packable core libraries (`/src`), example applications (`/apps`), an operator CLI (`/tools`), and tests (`/tests`).

There is no frontend in this repository. The event store is a library plus a terminal CLI.

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
dotnet test tests/Alberto.Tests/Alberto.Tests.csproj --filter "Category!=Integration"  # No Docker, ~2s
```

Postgres-backed tests use Testcontainers and need a running Docker daemon. They are all tagged
`[Trait("Category", "Integration")]`, so the filter above selects the in-memory suite alone.
`PostgresCluster` is an assembly fixture that starts its container **lazily**, on the first
request for a database, so a filtered run never reaches for Docker — keep it that way.

Every container in the repo starts through `ContainerStartup.StartNewAsync`
(`tests/Shared/ContainerStartup.cs`, compiled as a linked item into the three projects that
start containers), which retries the host-port collision that rootless Docker produces under
load. A bare `container.StartAsync()` is the bug. See
[docs/development/rootless-docker-ports.md](docs/development/rootless-docker-ports.md).

### Test quality
```bash
build/coverage.sh                       # Line/branch coverage, whole suite
build/mutation-test.sh                  # Stryker over the core packages
build/mutation-test.sh --since main     # Only what the branch changed (what PR CI gates on)
```

Coverage runs the whole suite; mutation testing runs `Category!=Integration` only, because it
re-runs the suite once per mutant. That is why `Alberto.Postgres`, `Alberto.Messaging.Postgres`
and `Alberto.EntityFramework` are **not** in the mutation set — scoring them without their
integration tests would report a fabricated number, not a low one. See
[docs/development/mutation-testing.md](docs/development/mutation-testing.md).

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
dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*Append*'
```

## Architecture

### Directory Structure
```
/src/                           # Core libraries: 10 packable, 3 not
  Alberto/                      # Event store abstractions, control loop, middleware
  Alberto.Commands/             # Command handling
  Alberto.Commands.Analyzers/   # Roslyn analyzers for the command pipeline  (not packable)
  Alberto.InMemory/             # In-memory backend (dev/test)
  Alberto.Postgres/             # PostgreSQL backend, migrations
  Alberto.EntityFramework/      # EF-backed projections
  Alberto.Messaging/            # Transactional outbox abstractions
  Alberto.Messaging.Postgres/   # PostgreSQL outbox store
  Alberto.Telemetry/            # OpenTelemetry instrumentation
  Alberto.Testing/              # `Spec` decider DSL + in-memory harness, for consumers
  Alberto.Testing.Xunit/        # Backend conformance specifications (xUnit v3)
  Alberto.Admin/                # IAdminReader/IAdminOperator: parked      (not packable)
  Alberto.Admin.Postgres/       # Its only implementation: parked          (not packable)

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
  Alberto.Tests/                # Unit + Testcontainers integration tests (xUnit 3)
  Alberto.Tests.SampleEvents/   # Sample [EventType] records in a separate assembly, for
                                #   assembly-scanning tests
  Alberto.Examples.Tests/       # Tests over the Orders/Payments examples, incl. GraphQL
                                #   schema snapshots
  Alberto.Orders.LoadTests/     # K6 load tests (TypeScript)

/benchmarks/
  Alberto.Benchmarks/           # BenchmarkDotNet append/read/checkpoint benchmarks
  Alberto.Benchmarks.Core/      # Shared harness: event plans, BDN import, result comparison
  Alberto.Benchmarks.Compare/   # CLI that diffs two benchmark runs and renders a report
```

Note: `apps/Alberto.Payments` is in the solution and builds, but it has no host of its own and is not orchestrated by the AppHost. Its slices are registered by the Orders API host, which serves their GraphQL fields alongside Orders'.

### Key Patterns
- **Event Sourcing with DCB**: Append-only event log with dynamic consistency boundaries
- **Vertical slices in the examples**: `apps/Alberto.Orders` and `apps/Alberto.Payments` are sliced by behaviour, not layer. One folder per slice under `Features/`, holding that slice's input type, state record, evolver, decision function, boundary and GraphQL operation. **Slices share the event log and nothing else**, no shared state record, no shared evolver, no base state. `Contracts/` (events, status enums, problem codes, tag keys) and `Platform/` (DI, `DbContext`, EF migrations) are the two deliberate exceptions, named so they cannot be mistaken for domain code that happens to be shared. Five slices fold `OrderCreated`, each projecting a different part of it; that duplication is the pattern working. See [docs/architecture/vertical-slices.md](docs/architecture/vertical-slices.md)
- **Unit-testing a slice**: `Spec.For(evolver).Given(events).When(state => Decider.Decide(...))` plus the `Then*` verbs, from `Alberto.Testing`. `Given` folds history through the real evolver, `ThenState` folds the emitted events on top. Framework-neutral: it throws `SpecificationException`, it never calls `Assert`. The example decider tests in `tests/Alberto.Examples.Tests` are all written this way and are the reference for how a slice's tests should read. **The chain is a type-level state machine, one class per stage under `src/Alberto.Testing/Deciders/`** (`Specification` → `HistorySpecification` → `DecisionSpecification`/`DecisionResultSpecification`, with the verbs on `DecisionAssertions`/`StatefulDecisionAssertions`): an illegal order does not compile, so there are no runtime ordering guards to add. Adding a verb means deciding which stage it belongs to, and a verb on a shared base must be legal at *every* stage that inherits it
- **Unit-testing a projection**: `ProjectionSpec.For(SomeProjection.Declaration).Given(events).When(events)` plus `ThenDocument`/`ThenNoDocument(s)`/`ThenDocumentCount`/`ThenDeleted`/`ThenUnchanged`/`ThenDocuments`, from `Alberto.Testing`. It runs the `ProjectionDeclaration` and **nothing else** — no `IStateStore`, no control loop, no database — which is why the same specification reads identically over a JSONB projection and an EF one. Context comes from `ForTenant`/`At`/`AtPosition`/`WithMetadata`; timestamps default to `ProjectionSpec.Epoch` and event ids are derived from the position, so a specification is deterministic. `AtPosition` is also how the `IProjectionEntity.LastProcessedPosition` redelivery guard is specified. **One document space, not one per tenant** — the runtime isolates tenants with a state store per tenant, so tenancy here is context and nothing more; asserting isolation between two tenants is two specifications. `Spec.For(evolver).Given(events).ThenState(...)` with no `When` specifies an evolver's fold on its own. Staged the same way as the decider DSL, under `src/Alberto.Testing/Projections/` (`ProjectionSpecification` → `ProjectionHistorySpecification` → `ProjectionOutcomeSpecification`, over an internal `ProjectionFold` every stage is a facade on): `ThenDeleted`/`ThenUnchanged` exist only after a `When`, `Given` only before one, and the context verbs on `ProjectionStage` at every stage
- **Async Processing**: `ControlLoop` polls the event log and dispatches through a middleware chain to projections/reactors. See [docs/architecture/async-processing.md](docs/architecture/async-processing.md)
- **Middleware**: `MiddlewareRunner` builds both the single-event (`ConsumeEventContext`) and batch (`BatchConsumeContext`) chains. Retry/dead-letter logic is shared via `RetryAndDeadLetterCore` behind `IMiddlewareContext`
- **Zero-downtime projection rebuilds**: opt in with `.WithRebuilds()`. `RebuildCoordinator` replays the log into a shadow copy of a projection's state under its own checkpoint, then swaps versions in one transaction. Driven by `alberto ops rebuild start|status|promote|abort`
- **Multi-Tenant**: X-Tenant-Id header propagation, tenant-isolated queries, tenant leases
- **Store imprint**: single-tenant and multi-tenant are two disjoint migration sets sharing one journal, so **`.WithTenancy()` cannot be added to or removed from a store that already has data.** There is no bridging script and no `tenant_id` backfill. `PostgresMigrator.Migrate` refuses the wrong set before running anything, throwing `AlbertoStoreMismatchException` (ALB0021). The mode is read from `alberto_store_imprint` (a table the migrator creates itself, since the check that must precede every script cannot depend on a script), falling back to sniffing `alberto_events` for a `tenant_id` column, which covers both stores predating the imprint and stores left half-migrated. A store with no `alberto_events` is fresh and may become either mode. Pointing a module at an *empty* database is deliberately **not** covered: nothing contradicts, so it migrates cleanly and serves an empty store
- **Projection tenancy**: a state store's tenancy is fixed when the store is built and decided by the schema, not by the caller: a module that declared `.WithTenancy()` is migrated with `tenant_id NOT NULL` in its primary key, so a store built without one fails every write with `42P10`, and the reverse mismatch fails with `42703`. `AddProjection` therefore takes a `Func<string?, IStateStore<TState>>`: the projection builds one store per tenant and routes each event to the store for the tenant it carries. A cross-tenant aggregate on a tenant-enabled module stores its single blended document under `TenantScope.CrossTenant` (`"*"`), which readers pass too: resolvers resolve the writer's own factory from DI (`{moduleKey}:{processorId}`) so the only thing they decide is which tenant to read
- **Tenant sharding** (opt-in, not the default): a module's tenants can be spread over several PostgreSQL databases with `.WithTenancy(t => t.AcrossPostgresDatabases(...))`, row-level tenancy still applying inside each one. Each shard is a complete module registered under the DI key `{moduleKey}#{shardId}` (composed only by `ShardKey`); `ShardRoutingEventStore` picks the database per call from `TenantShardResolver`, which reads the `alberto_tenant_shards` catalog in a separate control database. **Positions are per database. Never compare one shard's with another's.** See [docs/architecture/tenant-sharding.md](docs/architecture/tenant-sharding.md)
- **Discarding a rebuild version**: neither promotion nor abort deletes the version it discards. A reader resolves the active version and *then* queries it, so a flip that deleted the superseded rows would strand any reader holding the old number between those two steps. Instead the transition only makes the version unreachable; `RebuildCoordinator`'s sweep reclaims it via `IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync` once `ReclaimGracePeriod` (2× `ProjectionVersions.RefreshInterval`) has elapsed since `CompletedAt`. Abort gets the same treatment for a second reason: a shadow loop only learns of the abort on its next poll, so its last writes land after the transition and the sweep is what actually removes them
- **Leases and fencing**: checkpoint writes can be fenced against a held lease via `IFencedCheckpointStore`
- **Transactional outbox**: `IOutboxStore` with `pending → processing → delivered/failed`, claimed via `FOR UPDATE SKIP LOCKED` under a claim lease (`claim_id`, `claimed_by`, `claim_expires_at`). A relay that dies mid-delivery does not strand its row: `ClaimPendingAsync` re-claims any `processing` entry whose `claim_expires_at` has passed or was never set. `RetryFailedAsync` is a separate operator action and matches `failed` only
- **Message transports are pluggable, and Alberto ships none**: `IMessageTransport` is a three-method adapter seam. Alberto guarantees the message is durable and handed over at least once; everything past `PublishAsync` belongs to the application. No broker binding ships, for two reasons: a binding puts a broker client version in the release matrix for the sake of an adapter that is about twenty lines of code (a worked one lives in `tests/Alberto.Tests.Messaging.Rebus`), and it picks a winner among buses Alberto has no basis to prefer. Rebus, MassTransit, Wolverine and a hand-rolled client are indistinguishable from where Alberto sits, and `InMemoryTransport` is the only implementation in the repo. **There is also no inbound path, by design**: receiving belongs to a bus, whose handler turns a message into a command that appends events. That is a scope boundary, not a gap: Alberto is an event store, not a messaging framework. Postgres-as-broker is reached by giving your bus a Postgres transport, which is why `Alberto.Messaging.Postgres` is outbox-only. See [docs/architecture/message-transports.md](docs/architecture/message-transports.md)
- **GraphQL** (Orders example only): HotChocolate 15.x

### Admin surface
The operator surface is the CLI in `tools/Alberto.Cli`. There is no admin HTTP API. `src/Alberto.Admin` holds the `IAdminReader`/`IAdminOperator` abstraction the CLI's 14 command files are built on (it serves no endpoint) and `AddAlbertoPostgresAdmin` in `src/Alberto.Admin.Postgres` is its only implementation.

**The whole admin surface is parked, not missing, and that includes its two projects.** A GraphQL admin API, an MCP server, a React console and a BFF live on `feature/admin-surface`, held out of 1.0 so their field and tool names are not frozen by semver. `Alberto.Admin` and `Alberto.Admin.Postgres` are both `IsPackable=false` for the same reason: shipping `IAdminReader`/`IAdminOperator` at 1.0 would freeze the abstraction under semver before the things that consume it exist. They build, they are in the solution, they are tested, and the CLI references them by project. They just do not go to nuget.org. Unparking is `IsPackable=true` plus capturing `PublicAPI.Shipped.txt` (the analyzer is gated on `IsPackable`, so it is inert until then).

`Alberto.Admin.Postgres` exists only because `Alberto.Postgres` **is** packable. `PostgresAdminDataAccess`, `PostgresAdminOperator` and `PostgresAdminServiceCollectionExtensions` used to live there, which made its nupkg carry an unresolvable `Alberto.Admin` dependency and 33 public members returning parked types. The three files keep `namespace Alberto.Postgres` so no consumer's usings changed, and they reach back for internals (`SchemaQualifier`) via `InternalsVisibleTo`.

Do not rebuild the front doors on main. Extend that branch. Keep `IAdminReader`/`IAdminOperator` additive when changing them here, or the branch stops merging cleanly.

- **Per-processor mutations** go through the core interfaces: `ICheckpointStore` (`SaveAsync`, `ResetAsync`, `RewindAsync`) and `IDeadLetterStore` (`CountAsync`, `ClearAsync`, `MarkForRetryAsync`).
- **`PostgresAdminDataAccess`** (`src/Alberto.Admin.Postgres`) holds the inspection queries and the composite transactional mutations (`RetryByRewindAsync`, `ReleaseTenantLeasesAsync`) that span multiple tables and so cannot be composed from per-processor interfaces.
- `SaveAsync` is monotonic by design (`GREATEST`). `RewindAsync` is the deliberate escape hatch for operator-initiated rewinds and is the only way to move a checkpoint backwards.
- **Sharded modules**: `ShardResolver` turns `--shard`/`--all-shards` plus `.alberto/config.json` into the databases a command runs against. Reads fan out by default; mutations refuse without a selection. `alberto shards list|where|assign` manages the catalog. Shard connection strings live in config, never in the catalog table.

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

*(None currently. The two rebuild-window gaps that were listed here are closed. See "Discarding a rebuild version" above.)*
