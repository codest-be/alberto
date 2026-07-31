# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Alberto is a DCB (Dynamic Consistency Boundary) Event Store for .NET 10.0. The repository is a monorepo containing packable core libraries (`/src`), example applications (`/apps`), an operator CLI (`/tools`), and tests (`/tests`).

The event store is a library, a terminal CLI, and an admin surface (GraphQL + MCP) that hosts opt into. The React operator console under `apps/Alberto.Admin` is an example, not a shipped package.

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
- **Store imprint**: single-tenant and multi-tenant are two disjoint migration sets sharing one journal, so **`.WithTenancy()` cannot be added to or removed from a store that already has data** — there is no bridging script and no `tenant_id` backfill. `PostgresMigrator.Migrate` refuses the wrong set before running anything, throwing `AlbertoStoreMismatchException` (ALB0021). The mode is read from `alberto_store_imprint` — a table the migrator creates itself, since the check that must precede every script cannot depend on a script — falling back to sniffing `alberto_events` for a `tenant_id` column, which covers both stores predating the imprint and stores left half-migrated. A store with no `alberto_events` is fresh and may become either mode. Pointing a module at an *empty* database is deliberately **not** covered: nothing contradicts, so it migrates cleanly and serves an empty store
- **Projection tenancy**: a state store's tenancy is fixed when the store is built and decided by the schema, not by the caller — a module that declared `.WithTenancy()` is migrated with `tenant_id NOT NULL` in its primary key, so a store built without one fails every write with `42P10`, and the reverse mismatch fails with `42703`. `AddProjection` therefore takes a `Func<string?, IStateStore<TState>>`: the projection builds one store per tenant and routes each event to the store for the tenant it carries. A cross-tenant aggregate on a tenant-enabled module stores its single blended document under `TenantScope.CrossTenant` (`"*"`), which readers pass too — resolvers resolve the writer's own factory from DI (`{moduleKey}:{processorId}`) so the only thing they decide is which tenant to read
- **Tenant sharding** (opt-in, not the default): a module's tenants can be spread over several PostgreSQL databases with `.WithTenancy(t => t.AcrossPostgresDatabases(...))`, row-level tenancy still applying inside each one. Each shard is a complete module registered under the DI key `{moduleKey}#{shardId}` (composed only by `ShardKey`); `ShardRoutingEventStore` picks the database per call from `TenantShardResolver`, which reads the `alberto_tenant_shards` catalog in a separate control database. **Positions are per database — never compare one shard's with another's.** See [docs/architecture/tenant-sharding.md](docs/architecture/tenant-sharding.md)
- **Discarding a rebuild version**: neither promotion nor abort deletes the version it discards. A reader resolves the active version and *then* queries it, so a flip that deleted the superseded rows would strand any reader holding the old number between those two steps. Instead the transition only makes the version unreachable; `RebuildCoordinator`'s sweep reclaims it via `IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync` once `ReclaimGracePeriod` (2× `ProjectionVersions.RefreshInterval`) has elapsed since `CompletedAt`. Abort gets the same treatment for a second reason: a shadow loop only learns of the abort on its next poll, so its last writes land after the transition and the sweep is what actually removes them
- **Leases and fencing**: checkpoint writes can be fenced against a held lease via `IFencedCheckpointStore`
- **Transactional outbox**: `IOutboxStore` with `pending → processing → delivered/failed`, claimed via `FOR UPDATE SKIP LOCKED` under a claim lease (`claim_id`, `claimed_by`, `claim_expires_at`). A relay that dies mid-delivery does not strand its row: `ClaimPendingAsync` re-claims any `processing` entry whose `claim_expires_at` has passed or was never set. `RetryFailedAsync` is a separate operator action and matches `failed` only
- **GraphQL** (Orders example only): HotChocolate 15.x

### Admin surface

**This is the branch the admin surface lives on.** `main` ships the CLI and nothing else: the
GraphQL API, the MCP server, the React console and the BFF are held out of 1.0 so their field and
tool names are not frozen by semver. Everything below exists here and only here. Merge `main` into
this branch regularly — `main` keeps changing underneath it, and the longer the gap the more of the
merge is archaeology.

`Alberto.Dcb.Admin` and `Alberto.Dcb.Postgres.Admin` are `IsPackable=false` on `main` for the same
reason: shipping `IAdminReader`/`IAdminOperator` at 1.0 would freeze the abstraction before the
things that consume it exist. Unparking them is part of landing this branch — `IsPackable=true` plus
capturing `PublicAPI.Shipped.txt` (the analyzer is gated on `IsPackable`, so it is inert until then).

`Alberto.Dcb.Postgres.Admin` exists only because `Alberto.Dcb.Postgres` **is** packable.
`PostgresAdminDataAccess`, `PostgresAdminOperator` and `PostgresAdminServiceCollectionExtensions`
used to live there, which made its nupkg carry an unresolvable `Alberto.Dcb.Admin` dependency and 33
public members returning parked types. The three files keep `namespace Alberto.Dcb.Postgres` so no
consumer's usings changed, and they reach back for internals (`SchemaQualifier`) via
`InternalsVisibleTo`.

Three front doors over one set of operations — see [docs/admin.md](docs/admin.md).

- **CLI** (`tools/Alberto.Cli`) — talks straight to Postgres, needs no changes to the host.
- **GraphQL** (`src/Alberto.Dcb.Admin.GraphQL`, `AddAlbertoAdminGraphQL`) — every field takes an optional `module`; `null` means the default module. Only `adminDeadLetters`, `adminEvents` and `adminProjectionStates` take `tenant`. Subscription topics are module-scoped (`AdminTopics.ForModule`), and a multi-replica host needs a real backplane — the Orders example uses Postgres `LISTEN`/`NOTIFY`, not `AddInMemorySubscriptions`.
- **MCP** (`src/Alberto.Dcb.Admin.Mcp`, `AddAlbertoAdminMcp`) — 25 tools over stateless Streamable HTTP. Resolves the *unkeyed* reader/operator, so it only ever addresses the default module.

`AddAlbertoPostgresAdmin(moduleKey, schema, isDefault)` is called once per module and is the only implementation of `IAdminReader`/`IAdminOperator` — there is no in-memory admin. `isDefault` also registers the unkeyed pair. Registering only some modules makes the others invisible, not unsupported.

The React console (`apps/Alberto.Admin`) and its BFF (`apps/Alberto.Admin.Bff`) are **examples, not packages** — integrators copy them. The BFF is anonymous by default; `AdminBffAuthentication.AddAdminAuthentication` is the seam that turns on OIDC, and returning `true` from it is what flips the default authorization policy, maps `/bff/login`, and makes `/bff/user` answer 401 so the console renders a sign-in prompt.

Every mutation appends an `admin-*` event to the module's own log in the same transaction as the change, carrying the caller's `operatorId` (reserved `__admin__` tenant on multi-tenant stores). Readable afterwards via `adminEvents`, live via `onAdminAuditEvent`. The CLI does the same — `main` routes all of its mutations through `IAdminOperator` for exactly that reason, so the audit trail does not depend on which front door was used.

- **Per-processor mutations** go through the core interfaces: `ICheckpointStore` (`SaveAsync`, `ResetAsync`, `RewindAsync`) and `IDeadLetterStore` (`CountAsync`, `ClearAsync`, `MarkForRetryAsync`).
- **`PostgresAdminDataAccess`** (`src/Alberto.Dcb.Postgres.Admin`) holds the inspection queries and the composite transactional mutations (`RetryByRewindAsync`, `ReleaseTenantLeasesAsync`) that span multiple tables and so cannot be composed from per-processor interfaces.
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

*(The two rebuild-window gaps that were listed here are closed — see "Discarding a rebuild version" above.)*

- **The signed-in user does not reach the audit trail.** `apps/Alberto.Admin.Bff` forwards an `X-Alberto-Operator` header, but nothing on the API side reads it — mutations are attributed to the `operatorId` argument, defaulting to `admin-panel`. Wiring an identity provider does not currently change what the audit trail records.
- **MCP addresses only the default module** — its tools take no module argument and resolve the unkeyed reader/operator.
- **No `renameCheckpoint` mutation.** `IAdminOperator.RenameCheckpointAsync` is reachable from the CLI and the `alberto_rename_checkpoint` MCP tool, but has no GraphQL equivalent.
- **`ProjectionState` carries no document body**, so the console can list projection documents and their positions but cannot show one's contents.
