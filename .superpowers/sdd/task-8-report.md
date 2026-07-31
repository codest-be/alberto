# Task 8 Report: Postgres Provisioning Harness

## Seeding Timings (headline result)

Measured on Docker Desktop, `postgres:16-alpine`, Apple M-series host:

| Store size | Events | Wall-clock seed + first clone |
|---|---|---|
| Small | 10,000 | **3.37 s** |
| Medium | 100,000 | **13.89 s** |
| Large | 1,000,000 | **84.82 s** (~1 min 25 s) |

The Large seed completed in ~85 s, well inside the 10-minute threshold. The COPY-based seeding path is not required at this scale.

## What Was Implemented

Two files added to `benchmarks/Alberto.Dcb.Benchmarks/Harness/`:

**`StoreSizes.cs`** — static constants Small (10k), Medium (100k), Large (1M) with XML summary doc matching the brief verbatim.

**`BenchmarkDatabase.cs`** — the provisioning harness:
- Singleton process-wide instance via `Lazy<Task<BenchmarkDatabase>>`.
- Starts a `postgres:16-alpine` container with `max_connections=200`; supports external Postgres via `ALBERTO_BENCH_POSTGRES` env var (`IsExternal` reports which mode).
- Per-size template built once via `ConcurrentDictionary<int, Lazy<Task>>` — single build, all concurrent waiters share the same task; a failed build surfaces as one error to every waiter with no retry race.
- Template build connection uses `Pooling=false` (load-bearing; matches the pattern and comment in `PostgresCluster.cs`). The same connection string is passed to both `PostgresMigrator.Migrate` and `SeedAsync`, so no physical sessions remain attached to the template database when `BuildTemplateAsync` returns.
- Migration via `PostgresMigrator.Migrate(buildConnectionString, schema: null, singleTenant: true)` — handles both `Successful=false` and thrown `NpgsqlException`.
- Seeding in batches of 1,000 via `EventPlan.Build(storeSize, seed: 42)` → `AppendAsync`.
- `VACUUM ANALYZE` run at the end of `SeedAsync`, before the function returns (required for stable query plans across benchmark runs).
- Clones returned with `MaxPoolSize=10` to respect the 200-connection ceiling shared across workload classes.
- `NextDatabaseName` truncates the slug to `Math.Max(1, 63 - suffix.Length)` bytes (Postgres identifier cap).

## Verification Output (actual, not summarised)

Two Small clones from the same template, followed by Medium and Large seeding:

```
=== Small (10.000 events) ===
  Template built + clone 1 in 3,37s
  Clone 2 in 1,12s
  Clone 1 event count: 10.000  (expected 10.000) — PASS
  Clone 2 event count: 10.000  (expected 10.000) — PASS

=== Medium (100.000 events) ===
  Template built + clone in 13,89s

=== Large (1.000.000 events) ===
  (Will report elapsed time and stop if > 10 minutes)
  ... still seeding, 30s elapsed ...
  ... still seeding, 60s elapsed ...
  Template built + clone in 84,82s

=== Summary ===
  Small  ( 10.000 events):    3,37s
  Medium (100.000 events):   13,89s
  (Large reported above)
```

Second Small clone took 1.12 s (file-copy, not re-seed), confirming the template indirection is working. No "source database is being accessed by other users" error on either clone.

Throwaway verification project is in the scratchpad (`BenchVerify/`) — not committed.

## Build

```
dotnet build benchmarks/Alberto.Dcb.Benchmarks/Alberto.Dcb.Benchmarks.csproj -c Release
Build succeeded.  0 Warning(s).  0 Error(s).
```

## Files Changed

- `benchmarks/Alberto.Dcb.Benchmarks/Harness/StoreSizes.cs` (created)
- `benchmarks/Alberto.Dcb.Benchmarks/Harness/BenchmarkDatabase.cs` (created)

## Self-Review

**Completeness**
- All four specified public members present: `Instance`, `CloneAsync`, `PostgresImage`, `IsExternal`.
- Pooling disabled on template-build connection: confirmed. The `buildConnectionString` (with `Pooling=false`) is the only one passed into `SeedAsync`. The `NpgsqlDataSource` created from it inherits `Pooling=false`, so every `AppendAsync` connection and the VACUUM ANALYZE connection both close physically. After `await using var dataSource` disposes, no sessions remain.
- `VACUUM ANALYZE` runs at the end of `SeedAsync` before it returns.
- Seeding happens once per template: `Lazy<Task>` in `ConcurrentDictionary` guarantees single invocation.

**Concurrency**
- Two workload classes requesting the same template concurrently: `ConcurrentDictionary.GetOrAdd` with `Lazy<Task>` ensures `BuildTemplateAsync` is invoked exactly once. All concurrent awaits share the same task; a faulted task re-throws to every caller.

**Quality**
- Names describe what things do. Errors throw with context (template name, wrapped cause). No silent swallowing.

**Discipline**
- Only the two files specified in the brief were added. No benchmark classes added. No csproj modifications. `TreatWarningsAsErrors` and the comment explaining its absence are untouched. The throwaway verification project is outside the repo in the scratchpad and was not committed.

## Issues / Concerns

None. The Large seed at ~85 s is practical for nightly runs without needing the COPY path.
