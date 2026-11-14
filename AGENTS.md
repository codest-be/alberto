# AGENTS.md

This file provides guidance to coding agents (Codex, Claude Code and others) working in this repository.

## Project Overview

Alberto is a DCB (Dynamic Consistency Boundary) Event Store for .NET 10 and 11 (the libraries under `/src` multi-target `net10.0;net11.0` via `AlbertoLibraryTargetFrameworks` in `Directory.Build.props`; benchmarks and the other test projects are net10.0 only, and `Alberto.Tests` runs on both). The repository holds the packable core libraries (`/src`), tests (`/tests`) and benchmarks (`/benchmarks`), and nothing else.

The example applications (Orders and Payments, GraphQL on Postgres run by Aspire, plus their K6 load tests) live in [codest-be/alberto-examples](https://github.com/codest-be/alberto-examples) and consume Alberto from nuget.org. There is no operator tool and no admin surface: operators call the core interfaces (see "Operator surface" below).

## Build & Run Commands

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

### Benchmarks
```bash
dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*Append*'
```

## Architecture

### Directory Structure
```
/src/                           # Core libraries: 10 packable, 2 not
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
  Alberto.SourceGeneration/     # Source generator                         (not packable)

/tests/
  Alberto.Tests/                # Unit + Testcontainers integration tests (xUnit 3)
  Alberto.Tests.SampleEvents/   # Sample [EventType] records in a separate assembly, for
                                #   assembly-scanning tests
  Alberto.Tests.Messaging.Rebus/ # A worked IMessageTransport adapter over Rebus
  Alberto.AotSmoke/             # Console app published with PublishAot in CI and run against
                                #   Postgres; exits non-zero on failure (see "Native AOT" below)

/benchmarks/
  Alberto.Benchmarks/           # BenchmarkDotNet append/read/checkpoint benchmarks
  Alberto.Benchmarks.Core/      # Shared harness: event plans, BDN import, result comparison
  Alberto.Benchmarks.Compare/   # CLI that diffs two benchmark runs and renders a report
```

### Key Patterns
- **Event Sourcing with DCB**: Append-only event log with dynamic consistency boundaries
- **Unit-testing a slice**: `Spec.For(evolver).Given(events).When(state => Decider.Decide(...))` plus the `Then*` verbs, from `Alberto.Testing`. `Given` folds history through the real evolver, `ThenState` folds the emitted events on top. Framework-neutral: it throws `SpecificationException`, it never calls `Assert`. The decider tests in alberto-examples are all written this way and are the reference for how a slice's tests should read. **The chain is a type-level state machine, one class per stage under `src/Alberto.Testing/Deciders/`** (`Specification` → `HistorySpecification` → `DecisionSpecification`/`DecisionResultSpecification`, with the verbs on `DecisionAssertions`/`StatefulDecisionAssertions`): an illegal order does not compile, so there are no runtime ordering guards to add. Adding a verb means deciding which stage it belongs to, and a verb on a shared base must be legal at *every* stage that inherits it
- **Unit-testing a projection**: `ProjectionSpec.For(SomeProjection.Declaration).Given(events).When(events)` plus `ThenDocument`/`ThenNoDocument(s)`/`ThenDocumentCount`/`ThenDeleted`/`ThenUnchanged`/`ThenDocuments`, from `Alberto.Testing`. It runs the `ProjectionDeclaration` and **nothing else** — no `IStateStore`, no control loop, no database — which is why the same specification reads identically over a JSONB projection and an EF one. Context comes from `ForTenant`/`At`/`AtPosition`/`WithMetadata`; timestamps default to `ProjectionSpec.Epoch` and event ids are derived from the position, so a specification is deterministic. `AtPosition` is also how the `IProjectionEntity.LastProcessedPosition` redelivery guard is specified. **One document space, not one per tenant** — the runtime isolates tenants with a state store per tenant, so tenancy here is context and nothing more; asserting isolation between two tenants is two specifications. `Spec.For(evolver).Given(events).ThenState(...)` with no `When` specifies an evolver's fold on its own. Staged the same way as the decider DSL, under `src/Alberto.Testing/Projections/` (`ProjectionSpecification` → `ProjectionHistorySpecification` → `ProjectionOutcomeSpecification`, over an internal `ProjectionFold` every stage is a facade on): `ThenDeleted`/`ThenUnchanged` exist only after a `When`, `Given` only before one, and the context verbs on `ProjectionStage` at every stage
- **Async Processing**: `ControlLoop` polls the event log and dispatches through a middleware chain to projections/reactors. See [docs/architecture/async-processing.md](docs/architecture/async-processing.md)
- **Middleware**: `MiddlewareRunner` builds both the single-event (`ConsumeEventContext`) and batch (`BatchConsumeContext`) chains. Retry/dead-letter logic is shared via `RetryAndDeadLetterCore` behind `IMiddlewareContext`
- **Zero-downtime projection rebuilds**: opt in with `.WithRebuilds()`. `RebuildCoordinator` replays the log into a shadow copy of a projection's state under its own checkpoint, then swaps versions in one transaction. Driven through the module's keyed `IProjectionRebuildStore` (`StartAsync`, `ListAsync`, `RequestPromotionAsync`, `RequestAbortAsync`)
- **Multi-Tenant**: X-Tenant-Id header propagation, tenant-isolated queries, tenant leases
- **Store imprint**: single-tenant and multi-tenant are two disjoint migration sets sharing one journal, so **`.WithTenancy()` cannot be added to or removed from a store that already has data.** There is no bridging script and no `tenant_id` backfill. `PostgresMigrator.Migrate` refuses the wrong set before running anything, throwing `AlbertoStoreMismatchException` (ALB0021). The mode is read from `alberto_store_imprint` (a table the migrator creates itself, since the check that must precede every script cannot depend on a script), falling back to sniffing `alberto_events` for a `tenant_id` column, which covers both stores predating the imprint and stores left half-migrated. A store with no `alberto_events` is fresh and may become either mode. Pointing a module at an *empty* database is deliberately **not** covered: nothing contradicts, so it migrates cleanly and serves an empty store
- **Projection tenancy**: a state store's tenancy is fixed when the store is built and decided by the schema, not by the caller: a module that declared `.WithTenancy()` is migrated with `tenant_id NOT NULL` in its primary key, so a store built without one fails every write with `42P10`, and the reverse mismatch fails with `42703`. `AddProjection` therefore takes a `Func<string?, IStateStore<TState>>`: the projection builds one store per tenant and routes each event to the store for the tenant it carries. A cross-tenant aggregate on a tenant-enabled module stores its single blended document under `TenantScope.CrossTenant` (`"*"`), which readers pass too: resolvers resolve the writer's own factory from DI (`{moduleKey}:{processorId}`) so the only thing they decide is which tenant to read
- **Tenant sharding** (opt-in, not the default): a module's tenants can be spread over several PostgreSQL databases with `.WithTenancy(t => t.AcrossPostgresDatabases(...))`, row-level tenancy still applying inside each one. Each shard is a complete module registered under the DI key `{moduleKey}#{shardId}` (composed only by `ShardKey`); `ShardRoutingEventStore` picks the database per call from `TenantShardResolver`, which reads the `alberto_tenant_shards` catalog in a separate control database. **Positions are per database. Never compare one shard's with another's.** See [docs/architecture/tenant-sharding.md](docs/architecture/tenant-sharding.md)
- **Discarding a rebuild version**: neither promotion nor abort deletes the version it discards. A reader resolves the active version and *then* queries it, so a flip that deleted the superseded rows would strand any reader holding the old number between those two steps. Instead the transition only makes the version unreachable; `RebuildCoordinator`'s sweep reclaims it via `IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync` once `ReclaimGracePeriod` (2× `ProjectionVersions.RefreshInterval`) has elapsed since `CompletedAt`. Abort gets the same treatment for a second reason: a shadow loop only learns of the abort on its next poll, so its last writes land after the transition and the sweep is what actually removes them
- **Leases and fencing**: checkpoint writes can be fenced against a held lease via `IFencedCheckpointStore`
- **Transactional outbox**: `IOutboxStore` with `pending → processing → delivered/failed`, claimed via `FOR UPDATE SKIP LOCKED` under a claim lease (`claim_id`, `claimed_by`, `claim_expires_at`). A relay that dies mid-delivery does not strand its row: `ClaimPendingAsync` re-claims any `processing` entry whose `claim_expires_at` has passed or was never set. `RetryFailedAsync` is a separate operator action and matches `failed` only
- **Native AOT and trimming**: Alberto, Alberto.Commands, Alberto.Postgres, Alberto.Messaging, Alberto.Messaging.Postgres and Alberto.Telemetry set `IsAotCompatible` (and `EnableConfigurationBindingGenerator`), so an IL2xxx/IL3xxx warning fails their build. Every event-type fact (id, version, tags, JSON contract) is read through an `IEventTypeRegistry`: `WithEventsFrom(assembly)` builds one by reflection and is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`; `WithEvents(registry)` takes one built with `EventTypeRegistry.CreateBuilder()` and `JsonTypeInfo` from the app's own `JsonSerializerContext`, and is the AOT path. Any other API that serializes a caller's type by reflection is annotated the same way and has a `JsonTypeInfo<T>` overload (`PostgresStateStore<TState>`, messaging `Map`). The reflection-parity test in `EventTypeRegistryTests` and `tests/Alberto.AotSmoke` (the `aot-smoke` CI job) are what keep the two paths equal. The builder's typed overloads (`Add(ctx.Default.X, e => [new("order", e.OrderId)])`) take the tag extractor without a cast; because a hand-written extractor restates the `[Tag]` attributes, `EventTypeRegistryVerifier.Verify(registry)` in Alberto.Testing checks the two agree (plus id and version against `[EventType]`) and belongs in one test per shipped registry. Do not "fix" a new analyzer warning with a suppression unless the justification is a real guarantee, as in `ReflectionJson` (feature switch) or `AlbertoConfigurationScanner` (type-level annotation)
- **Message transports are pluggable, and Alberto ships none**: `IMessageTransport` is a three-method adapter seam. Alberto guarantees the message is durable and handed over at least once; everything past `PublishAsync` belongs to the application. No broker binding ships, for two reasons: a binding puts a broker client version in the release matrix for the sake of an adapter that is about twenty lines of code (a worked one lives in `tests/Alberto.Tests.Messaging.Rebus`), and it picks a winner among buses Alberto has no basis to prefer. Rebus, MassTransit, Wolverine and a hand-rolled client are indistinguishable from where Alberto sits, and `InMemoryTransport` is the only implementation in the repo. **There is also no inbound path, by design**: receiving belongs to a bus, whose handler turns a message into a command that appends events. That is a scope boundary, not a gap: Alberto is an event store, not a messaging framework. Postgres-as-broker is reached by giving your bus a Postgres transport, which is why `Alberto.Messaging.Postgres` is outbox-only. See [docs/architecture/message-transports.md](docs/architecture/message-transports.md)

### Operator surface
There is no operator tool and no admin API. Operators resolve the core interfaces from DI by module key (or `ShardKey.Compose(moduleKey, shardId)` on a sharded module) and call them; inspection is plain SQL. [docs/operations.md](docs/operations.md) is the reference. The CLI and the parked `Alberto.Admin` projects were removed; they remain in git history, and `feature/admin-surface` no longer merges cleanly.

- **Per-processor mutations**: `ICheckpointStore` (`SaveAsync`, `ResetAsync`, `RewindAsync`), `IDeadLetterStore` (`GetAsync`, `CountAsync`, `ClearAsync`, `MarkForRetryAsync`), `IProjectionRebuildStore`, `ITenantShardMap` (`AssignAsync` is first-writer-wins) and `IOutboxStore` (`RetryFailedAsync`, `PurgeDeliveredAsync`).
- `SaveAsync` is monotonic by design (`GREATEST`). `RewindAsync` is the deliberate escape hatch for operator-initiated rewinds and is the only way to move a checkpoint backwards. A processor rename is `SaveAsync` of the new id at the old position, then `ResetAsync` (which deletes the row) of the old id.
- **No composite mutations**: releasing tenant leases is a SQL `DELETE` (or waiting out `LeaseDuration`), and retry-by-rewind is `RewindAsync` then `ClearAsync`, which is not one transaction.

## Technology Stack

| Layer | Technology |
|-------|------------|
| Framework | .NET 10.0 and 11.0 (libraries) |
| Database | PostgreSQL (Npgsql 10.0.3) |
| Migrations | DbUp-PostgreSQL 7.0.1 (event store), EF Core 10.0.15 on both targets (`Alberto.EntityFramework`); stays 10.x until the Npgsql EF provider ships 11.0.0 GA |
| Observability | OpenTelemetry 1.21.0 |
| Testing | xUnit v3 3.2.2, FluentAssertions 8.11.0, Testcontainers 4.16.0 |

## Package Management

- **NuGet**: Centralized versions in `Directory.Packages.props`. Runtime-tied packages (Microsoft.Extensions.*, System.Text.Json) are declared once per target framework there
- **Dependabot**: `.github/dependabot.yml` groups updates by package family; majors arrive as their own PRs

## Configuration

- **Solution file**: `Alberto.slnx` (modern .NET format)
- **Build settings**: `Directory.Build.props`

## Known Gaps

Documented so they are not mistaken for working features:

*(None currently. The two rebuild-window gaps that were listed here are closed. See "Discarding a rebuild version" above.)*
