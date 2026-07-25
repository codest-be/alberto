# Test container consolidation — design

**Date:** 2026-07-25
**Scope:** `tests/Alberto.Dcb.Tests`
**Status:** approved, not yet implemented

## Problem

The test suite starts roughly twenty PostgreSQL containers per run: seventeen
owned by `IClassFixture` fixtures and three created inline. Each container runs
its own postmaster, WAL writer and shared buffers, and each pays a container
start plus a full DbUp migration.

The container count is not the only cost. Nine of those containers are created
by fixtures that are byte-for-byte identical:

```csharp
await _container.StartAsync();
var result = PostgresMigrator.Migrate(_container.GetConnectionString(), singleTenant: true);
DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
```

`SingleTenantPostgresFixture`, `PostgresAdminDataAccessFixture`,
`PostgresOutboxStoreFixture` and `PostgresProjectionRebuildStoreFixture` are
that code verbatim under four names; `ProjectionRebuildHostFixture` adds a DI
host on top of it. Twenty containers express four distinct schema setups.

| Flavor | Setup | Test classes |
|---|---|---|
| **A** | `Migrate(cs, singleTenant: true)` | 9 |
| **B** | `Migrate(cs)` | 4 |
| **C** | EF `EnsureCreatedAsync` | 2 |
| **D** | EF `EnsureCreatedAsync`, then `Migrate(cs, singleTenant: true)` | 1 |
| inline | pristine, or NOTIFY-specific | 3 |

## Goals

Three criteria, all of which the design must satisfy:

1. **Wall-clock** — the suite must not get slower.
2. **Docker resource pressure** — fewer concurrent containers, since resource
   pressure is the suspected driver of the intermittent failures.
3. **Topology** — one fixture type per distinct setup, not nine for four.

## Rejected alternatives

**`ICollectionFixture` per existing fixture type.** Reduces twenty containers to
twelve but serializes the four-class and three-class groups, making wall-clock
worse. It also leaves all nine duplicate fixture classes in place, so it
addresses goal 2 only, and partially.

**Four flavor fixtures shared via `[assembly: AssemblyFixture]`.** Reaches seven
containers with parallelism intact, but nine test classes would then share one
database concurrently. Isolation drops from "own container" to "own
`Guid`-namespaced rows" — true today, but it becomes a standing invariant every
future test must honor. `ProjectionRebuildEndToEndTests` could not join at all:
it runs a live control loop over the whole event log under a hardcoded processor
id, so any other class's events would feed into it.

## Design

One container for the assembly. One *database* per test class, cloned from a
per-flavor template that is migrated once.

```
[assembly: AssemblyFixture(typeof(PostgresCluster))]

PostgresCluster                     1 container, 4 lazily-built templates
   |  CloneAsync(flavor, label) -> connection string for a fresh database
   +-- SingleTenantPostgresFixture       (A)
   +-- PostgresAdminDataAccessFixture    (A)
   +-- PostgresOutboxStoreFixture        (A)
   +-- PostgresProjectionRebuildStoreFx  (A)
   +-- ProjectionRebuildHostFixture      (A)
   +-- PostgresFixture                   (B)
   +-- MultiTenantPostgresFixture        (B)
   +-- EfProjectionTestFixture           (C)
   +-- StateStoreRebuildVersionFixture   (D)
```

### Why this shape

**The isolation boundary does not move.** Today each test class gets a private
database by way of a private container; here it still gets a private database.
No existing test needs auditing for cross-class interference, no new invariant
is imposed on tests written later, and `ProjectionRebuildEndToEndTests` keeps its
control loop alone in its own event log.

**Parallelism is untouched.** Nothing is placed in a shared collection, so
xUnit's class-level parallelism is exactly what it is today.

**Templates are lazy.** A filtered run pays for the templates it touches, not
all four.

### Verified mechanics

Both load-bearing assumptions were checked empirically before this design was
accepted, not assumed:

- `CREATE DATABASE ... TEMPLATE` inherits schema and data, isolates writes
  between clones, and nine concurrent clones from one template complete in
  **222 ms** total on `postgres:16-alpine`.
- xUnit v3 3.2.2 injects an `[assembly: AssemblyFixture]` instance into a
  **class fixture's** constructor, and into a test class constructor alongside a
  class fixture. This is the seam the design depends on.

### The critical detail

Templates must be built over connection strings carrying `Pooling=false`.

`PostgresMigrator.Migrate` and EF's `EnsureCreatedAsync` both go through Npgsql,
where closing a connection returns it to the pool while leaving it *physically*
open. A pooled connection lingering against a template makes every subsequent
clone fail:

```
ERROR:  source database "tmpl_a" is being accessed by other users
DETAIL:  There is 1 other session using the database.
```

This was reproduced directly. It is the highest-risk item in implementation.

### Components

**`PostgresCluster`** — the assembly fixture. Starts one `postgres:16-alpine`
container with `max_connections` raised. Builds each template on first request,
once, behind an async gate. Exposes
`Task<string> CloneAsync(TemplateFlavor flavor, string label)`, returning a
connection string for a freshly cloned database.

**Per-class fixtures** — keep their current names and public surfaces
(`DataSource`, `ConnectionString`, `Services`, helper methods). Only
`InitializeAsync` changes: it asks the cluster for a database instead of
starting a container. The four flavor-A duplicates collapse onto one shared base
with thin subclasses, removing the copy-paste without renaming anything.

**Unchanged** — the three inline-container tests keep their own containers, with
a comment recording why. `MigrationUpgradeAndParityTests` exercises the migrator
itself and needs a pristine, unmigrated database; `PostgresEventListenerTests`
needs its own NOTIFY listener.

**Deleted** — `Subscriptions/TempSweepRaceRepro.cs`. Its own doc comment reads
"TEMPORARY — probe for the coordinator sweep racing a freshly started rebuild.
Delete before committing." It was swept into commit `1144b69` by accident. It
loops sixty rounds and holds a container.

### Blast radius

No test class bodies change and no test class attributes change. `IClassFixture<T>`
declarations and constructors stay as they are, so the diff is confined to
fixture internals plus one new file.

### Error handling

A failed template build is cached and rethrown, so nine classes waiting on
flavor A surface one clear migration error rather than nine racing retries.

### Connection budget

Sixteen class-level pools now share one postmaster rather than sixteen. The
container runs with `max_connections=200` and each class data source caps
`Max Pool Size`, keeping worst-case demand under the limit.

### Database naming

Clone names are derived from the requesting fixture plus a short unique suffix,
lowercased and truncated to Postgres' 63-byte identifier limit.

## Verification

- Baseline captured before the change: suite wall-clock and peak concurrent
  container count.
- The same measurements after.
- Ten consecutive full-suite runs, to confirm the flake rate has not regressed.
  The design changes the concurrency profile, and flakiness is one of the three
  goals, so a single green run is not sufficient evidence.

## Expected outcome

Roughly twenty containers to four. DbUp runs sixteen times to four.
Class-level parallelism unchanged.

## Out of scope

The `xunit.runner.json` parallelism cap from the handoff. It throttles the
resource pressure this design removes at the source. Revisit only if the
measurements say otherwise.

The known gaps recorded in `CLAUDE.md` (promotion visibility window, aborted
version row lag, orphaned outbox entries, JSONB read-side tenant mismatch) are
untouched.
