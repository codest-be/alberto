# Benchmark suite — design

**Date:** 2026-07-26
**Scope:** `benchmarks/` (rewritten), `comparison/` (new), `.github/workflows/benchmarks.yml` (new),
`docs/benchmarks/` (new), one `InternalsVisibleTo` in `src/Alberto.Dcb`
**Status:** approved, not yet implemented

## Problem

`benchmarks/Alberto.Dcb.Benchmarks` contains three BenchmarkDotNet classes —
`AppendBenchmarks`, `ReadBenchmarks`, `CheckpointBenchmarks` — written as
before/after baselines for the P0–P5 audit. They have two defects that make them
unusable as a regression gate.

**They measure the wrong backend.** All three run against
`InMemoryEventStoreBackend`. The project references only `Alberto.Dcb` and
`Alberto.Dcb.InMemory`. Nothing in the suite exercises the SQL that shipping
users run, which is exactly where the audit's own findings lived — `SQL-1`
(multi-tag `DISTINCT`-before-`LIMIT` over-scan), `SQL-6` (types-or-tags driving
from the events table with a double `LEFT JOIN`), `PERF-3` (`GetOrdinal` by name
per column per row). `ReadBenchmarks` argues the InMemory backend mirrors the
Postgres algorithmic structure closely enough that relative deltas transfer.
That is a plausible claim and it is not a measurement.

**No result has ever been kept.** `artifacts/` is gitignored, no benchmark job
exists in CI, and there is no committed baseline anywhere in the tree. The
DCBQuery pipeline design names these benchmarks as a merge gate — "read and
append benchmarks show no regression on the `For(concept, id)` path" — against a
baseline that does not exist.

A third gap is documented but unaddressed: `CachingCheckpointStore` is
`internal`, so the "40–50× DB write reduction" claim in the docs is asserted by
no benchmark at all.

## Goals

1. A regression gate for Alberto against its own history, compared only within a
   consistent machine profile, with committed baselines.
2. Coverage of the full Postgres surface, not a proxy for it.
3. One headline throughput number — sustained events/sec end to end.
4. A defensible head-to-head against Marten.

## Non-goals

- Comparison against EventStoreDB/Kurrent, or any store outside .NET+Postgres.
- Per-PR benchmark gating.
- Capacity/sizing guidance for user deployments.

## Decisions taken

| Decision | Choice | Consequence accepted |
|---|---|---|
| Primary purpose | Regression gate first | Comparison is secondary and narrower |
| Backend | **Postgres only** | Every run needs Docker; runs take minutes; container/IO noise means the gate resolves ~20% shifts, not ~3% |
| Existing InMemory suite | Deleted, workloads designed fresh | Audit-era baselines are not carried forward |
| Result storage | Committed JSON keyed by machine profile | Trend history lives in git and is diffable |
| Cadence | Nightly CI + manual dispatch | PR CI stays fast; daily trend has shared-runner jitter |
| Comparison | Purpose-built Marten parity harness | Marten's own suite cannot produce a number (see below) |

## Architecture

```
benchmarks/
  Alberto.Dcb.Benchmarks/            # BenchmarkDotNet micro-benchmarks
    Harness/                         # provisioning, seeding, config
    Workloads/{Append,Query,Checkpoint,StateStore,Outbox,Tenancy,Sharding}/
  Alberto.Dcb.Benchmarks.Throughput/ # macro events/sec harness (not BDN)
  Alberto.Dcb.Benchmarks.Compare/    # baseline diff + accept tool
  results/                           # committed JSON, keyed by machine profile
comparison/                          # Marten parity harness; NOT in AlbertoV3.slnx
```

### Provisioning and seeding

`BenchmarkDatabase` honours `ALBERTO_BENCH_POSTGRES` if set, and otherwise
starts one Testcontainers Postgres per process. It applies the DbUp migrations
into **template databases** — one per schema variant × store size (`bench_st_10k`,
`bench_st_100k`, `bench_st_1m`, and the `bench_mt_*` multi-tenant equivalents) —
seeded once per process. Each benchmark class then issues
`CREATE DATABASE … TEMPLATE …` in `[GlobalSetup]`, so per-class setup costs about
a second instead of a re-seed.

This mirrors `tests/Alberto.Dcb.Tests/Infrastructure/PostgresCluster.cs`,
including its load-bearing constraint that connections used to build a template
must have pooling disabled — a pooled connection left open prevents the database
from being used as a template.

Two properties decide whether the numbers mean anything:

**`VACUUM ANALYZE` after seeding.** Without current statistics the planner
chooses different plans between runs, and the suite measures the planner's mood
rather than the code.

**Write workloads reset between iterations.** An append benchmark that does not
clean up is appending into a table that grows across iterations, so iteration 50
is not measuring what iteration 1 measured. Every write family gets an
`[IterationCleanup]` deleting back to the seeded head. This is cheap relative to
the iteration and it is what keeps a run internally comparable.

### BenchmarkDotNet configuration

A shared `BenchmarkConfig`:

- `RunStrategy.Monitoring` with reduced warmup and iteration counts. The work is
  IO-dominated, so BDN's default statistical machinery mostly burns wall-clock.
- `[MemoryDiagnoser]` retained. Allocation counts stay near-deterministic even
  when timing is noisy, so the `PERF-7` class of bug (an ArrayPool rental leaked
  per append) remains detectable.
- `JsonExporter.Full` — the machine-readable output the rest of the pipeline
  consumes.
- `[BenchmarkCategory]` per family, so `--anyCategories=append` works.

## Workload catalogue

Eight families. The `StoreSize` axis (10k/100k/1M) is applied **only where table
size can plausibly change the answer**; appends that do not read do not get it.
Applying it everywhere would triple nightly runtime for no signal.

| Family | Cases | Axes |
|---|---|---|
| **Append** | single (baseline), batch, with-DCB-check no-conflict, conflict-throw, tag fan-out, concurrent writers on disjoint boundaries, concurrent writers on the same boundary | batch 10/100/1000; tags 1/5/20; writers 1/4/16. `StoreSize` on the DCB-check and conflict cases only |
| **Query** | from-zero catch-up (baseline), tail read (`afterPosition = head − page`), by-type, by-tag, type∩tag, multi-tag union (2/8 tags), boundary read, `GetLastPositionAsync`, `GetStableHeadAsync` | `StoreSize` on all; page 500 |
| **Checkpoint** | save, get, fenced vs unfenced save, multi-processor flush | processors 1/10/100 |
| **State store** | load, save, read-modify-write per event, versioned read while a rebuild is active | small vs large state document |
| **Outbox** | enqueue, `ClaimPendingAsync` under N relays, mark-delivered batch, re-claim of expired `processing` rows | relays 1/4/16 |
| **Tenancy** | append and boundary read with `tenant_id` on; cross-tenant aggregate write | tenants 1/100 |
| **Sharding** | resolver hop + append vs direct append | shards 2 |
| **Throughput** (macro) | sustained events/sec through poll → middleware → projection → checkpoint | 1 projection / 10 projections / 100 tenants |

Three cases exist because the current suite misses them and they matter:

- **Tail read.** `afterPosition = head − page` is the polling steady state and
  takes a different query plan than reading from position 0. It is the read
  Alberto performs most often in production and the one the suite never measured.
- **Boundary read.** A small, selective query before a decision. This is the
  latency a user actually feels.
- **Concurrent writers on the same boundary.** Prices contention and
  serialization, which single-threaded appends cannot show.

The macro family reports events/sec, per-batch p50/p95, end-to-end lag, and the
**actual checkpoint write count** — which puts a measured number on the "40–50×
DB write reduction" claim instead of an assertion.

Approximately 80 cases. Seeding the 1M template dominates cold start; steady-state
nightly runtime is estimated at 30–60 minutes.

**Required change outside `benchmarks/`:** an `InternalsVisibleTo` for the
benchmark assembly, so `CachingCheckpointStore` can be measured directly. This
closes the gap recorded in `CheckpointBenchmarks.cs`.

It goes in `src/Alberto.Dcb/Alberto.Dcb.csproj` as an MSBuild item
(`<InternalsVisibleTo Include="Alberto.Dcb.Benchmarks" />`) alongside the six
existing entries — **not** in `AssemblyInfo.cs`, which holds only a comment
pointing at the csproj.

## Results storage and comparison

```
benchmarks/results/
  profiles/ci-ubuntu-x64.json
  ci-ubuntu-x64/
    baseline.json
    history/2026-07-26T02-12Z-a1b2c3.json
  local-9f3e/…
  comparisons/                       # Marten parity runs, same schema
```

**Profile keying is the honesty guard.** A profile records OS, architecture, CPU
model, logical cores, RAM, .NET SDK version, Postgres image tag, and
container-vs-external; its hash names the directory. The compare tool
**hard-errors on a cross-profile comparison** rather than warning. A laptop run
silently diffed against a CI baseline is worse than no trend line, because it
looks like data.

**One normalized schema, two producers.** BenchmarkDotNet's JSON and the macro
throughput harness both project into:

```json
{ "schemaVersion": 1,
  "run": { "timestamp": "…", "gitSha": "…", "profileId": "ci-ubuntu-x64", "albertoVersion": "…" },
  "measurements": [
    { "id": "Query.StreamByMultiTag", "params": { "StoreSize": 1000000, "Tags": 8 },
      "meanNs": 1234.5, "stdDevNs": 45.6, "opsPerSec": 810.0, "allocatedBytes": 456 }
  ] }
```

The Marten parity harness emits the same shape, which is what lets one tool
compare all three sources.

**Compare tool.** `compare --baseline … --candidate …` prints a delta table and
exits non-zero past threshold. The thresholds are deliberately asymmetric:

| Metric | Threshold | Why |
|---|---|---|
| Mean | +20% | Postgres in a container on a shared runner is a noisy instrument |
| Allocated bytes | +10% | Allocation counts are near-deterministic; a tight gate is defensible |

A regression must exceed the threshold **and** fall outside the combined standard
deviation band before it fires. Added or removed benchmarks are reported, never
failed.

**Promotion is manual.** CI appends to `history/` and reports; it never rewrites
`baseline.json`. A human runs `compare --accept`. An auto-promoting baseline
ratchets silently and stops being a gate.

## CI

New `.github/workflows/benchmarks.yml`, separate from `ci.yml`:

- `schedule` at 02:00 UTC, plus `workflow_dispatch` with inputs for category
  filter and a store-size cap, so a ten-minute append-only run is dispatchable.
- Builds, runs the micro suite and the macro harness, normalizes both into
  `candidate.json`, runs `compare` against the committed baseline, renders the
  delta table into the job summary, uploads raw BDN reports as artifacts, and
  fails the job on a threshold breach.
- On scheduled runs only, commits the normalized result into `history/` on
  `main` (`contents: write`, marked `[skip ci]`).

PR CI is untouched except for the smoke run described under Verification.

A shared GitHub runner with a Postgres container is a noisy measuring
instrument. This trend catches structural regressions and will not resolve small
ones. The remedy, if it is ever wanted, is a self-hosted runner on fixed
hardware — which the design already supports, since that is just another machine
profile.

## Comparison against Marten

### What the survey found

Neither major .NET event-sourcing option publishes reproducible figures.

- **Marten publishes no benchmark numbers.** Its public performance claims are
  qualitative: "Quick Append" described as "like 2X faster in our testing"
  ([Jeremy D. Miller, June 2025](https://jeremydmiller.com/2025/06/02/making-event-sourcing-with-marten-go-faster/)),
  and a 40–50% append-time reduction. Its own
  [bulk-events issue #3307](https://github.com/JasperFx/marten/issues/3307)
  states the tests are "not at all scientific" and that no specific durations are
  given for that reason. The [optimization docs](https://martendb.io/events/optimizing)
  carry no numbers.
- **EventStoreDB/Kurrent's 15k writes/sec and 50k reads/sec are marketing-page
  claims.** Asked for the methodology, the official answer was that benchmarks
  were in internal development with no public deadline
  ([forum thread](https://discuss.kurrent.io/t/where-to-find-information-about-the-eventstoredb-benchmarks/3618));
  they remain unpublished.
- The one head-to-head carrying real numbers is a
  [community forum post](https://discuss.kurrent.io/t/eventstoredb-performance-comparison/5068)
  (December 2023, i7-13700K, .NET 8, both under Testcontainers) reporting a
  single sequential append at ~14,909µs for EventStoreDB against ~1,137µs for
  Marten. A 13× gap on single appends, measured in Docker, with a responder in
  the thread cautioning that it does not reflect production, reads as a
  configuration artifact rather than a property of either product. It is not
  cited as evidence.

### Why Marten's own suite cannot be run

`src/MartenBenchmarks` is BenchmarkDotNet-based and contains `BulkLoading`,
`DocumentActions`, `LinqActions`, and `EventActions`. **`EventActions.AppendEvents`
appends zero events as checked into master.** `Setup()` scans
`src/Marten.Testing/CodeTracker/*.json`; that directory holds eight `.cs` files
and no JSON, and `GithubDataRecorder` — which would produce the data — has its
Octokit import commented out. `AllProjects` is empty, `SelectMany(...).Take(1000)`
yields an empty array, and the benchmark measures opening a `LightweightSession`
and calling `SaveChanges()` with nothing in it.

Three further observations, verified against a clone at the time of writing:

- `Program.cs` has `BenchmarkSwitcher` commented out and hard-codes all four
  classes, so `--filter` does not work unmodified.
- The LINQ materializing the event array runs inside the timed method.
- Each invocation appends to a new stream with no reset, so the events table
  grows across iterations.

`Directory.Build.props` targets `net9.0;net10.0`, and `ConnectionSource` reads
`marten_testing_database` or falls back to `localhost:5432/marten_testing`, so
provisioning either side is straightforward.

### The parity harness

`comparison/` holds a purpose-built harness implementing the same small set of
workloads on both libraries: append 1000 events to one stream, batch append at
10/100/1000, and a stream read. It is **excluded from `AlbertoV3.slnx` and from
CI**, and references **Marten from NuGet at a pinned version** rather than a
clone. It is version-controlled rather than kept in a scratch directory because a
comparison nobody can re-run is a screenshot, not evidence.

Parity rules, all of which are recorded in the published result:

- Same machine, same Postgres image and settings, same TFM (`net10.0`), same BDN
  job configuration, same event payload bytes, same batch sizes.
- Event arrays are pre-built in setup on **both** sides. This deviates from
  Marten's own harness, which materializes inside the timed method; the deviation
  is documented rather than silently adopted.
- Both sides reset between iterations.
- Alberto's parity cases run with the DCB conflict check **off**, because
  Marten's plain append has no equivalent. A second variant with the check on is
  reported separately as the cost of DCB — never as a Marten comparison.

Results land in `benchmarks/results/comparisons/` with the pinned Marten version,
the machine profile id, and the date. `docs/benchmarks/landscape.md` carries the
methodology, the deviations, and the survey findings above.

## Verification

The benchmarks themselves are not tested, but the **compare tool is real logic** —
threshold arithmetic, standard-deviation banding, profile matching, schema
parsing — and it is what stands between a regression and a false all-clear. It is
built test-first with unit tests in `tests/Alberto.Dcb.Tests`, which therefore
takes a project reference to `Alberto.Dcb.Benchmarks.Compare`. That pulls the
compare tool into the CI build graph, since `ci.yml` builds the test project — a
wanted side effect: the tool stays compiling even when no benchmark runs.

Two further guards:

- **Seed determinism.** A test asserting that the same seed produces identical
  row counts and tag distribution, so today's reseeded template is comparable to
  yesterday's.
- **`--job dry` smoke run on PR CI**, against the smallest template only. It
  takes seconds, uses the Docker already present on `ubuntu-latest`, and stops
  the benchmark project from bit-rotting between nightly runs — which is how
  benchmark suites usually die.

## Phasing

The workload families are independent and land incrementally rather than as one
commit.

| Phase | Contents |
|---|---|
| 1 | Harness, provisioning, template seeding, BDN config, result schema, compare tool (test-first), Append + Query families |
| 2 | Nightly workflow, PR smoke run, first committed baseline |
| 3 | Checkpoint (incl. `InternalsVisibleTo`), State store, macro Throughput harness |
| 4 | Outbox, Tenancy, Sharding |
| 5 | Marten parity harness, `docs/benchmarks/landscape.md` |

## Risks

**Shared-runner noise may produce false alarms.** Mitigated by loose timing
thresholds, the standard-deviation band requirement, and manual baseline
promotion. If alarms still prove noisy, the escalation is a self-hosted runner,
not looser thresholds.

**A 30–60 minute nightly is long enough that failures get ignored.** Mitigated by
the dispatchable store-size cap and category filters, so investigation does not
require a full run.

**The 1M seed may dominate cost more than estimated.** If so, the store-size axis
drops to 10k/100k and 1M becomes a weekly run. This is a runtime knob, not a
design change.
