# Benchmarks

Postgres-backed BenchmarkDotNet suite. Latest results and interpretation:
[docs/benchmarks/results.md](../docs/benchmarks/results.md)

## Running

Everything (needs Docker; takes 30–60 minutes cold). The `--filter '*'` is required:
without a filter BenchmarkDotNet prompts for a selection instead of running:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*'

Part of that time is warm-up: every case drives its own measured method up to 2000 times, or
15 seconds, before BenchmarkDotNet starts timing. It is load-bearing rather than padding:
see [Harness/Warmup.cs](Alberto.Benchmarks/Harness/Warmup.cs) for what it fixes and the
measurements behind the two constants.

One family:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --anyCategories=append

Against an existing Postgres rather than Testcontainers:

    ALBERTO_BENCH_POSTGRES="Host=localhost;Database=bench;Username=postgres;Password=postgres" \
      dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*'

## Comparing

Normalize a BenchmarkDotNet report, then diff it against the committed baseline. Point
`--import` at the whole results directory. A full run writes one report per benchmark
class, and importing a single file would compare a fraction of the suite:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --import BenchmarkDotNet.Artifacts/results --postgres-image postgres:16-alpine \
      --out candidate.json

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json

`--postgres-image` is required, and must name the image the run actually used
(`postgres:16-alpine`, from `BenchmarkDatabase`). It is part of the machine profile, so
importing with the wrong value produces a profile no baseline matches. Use
`--external-postgres` instead when the run went against `ALBERTO_BENCH_POSTGRES`.

Exit code 1 means a regression. Thresholds: mean +20% (and outside the combined standard
deviation band), allocations +10% (no noise band, since allocation counts do not drift).

## Baselines

Results are keyed by machine profile. Comparing across profiles is refused, not warned about,
so your laptop's numbers never silently diff against CI's.

Promotion is manual and deliberate:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json --accept

CI appends to `history/` on every nightly run but never touches `baseline.json`.

## Concurrency

Everything above runs single-threaded on one connection — ops/sec there is a latency
reciprocal, not a throughput ceiling. Three classes answer the concurrency questions instead:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- \
      --filter '*ConcurrentAppendBenchmarks*' '*AppendWithDcbCheckStoreSizeBenchmarks*'

- **`ConcurrentAppendBenchmarks`** (`[Params] Writers` = 1/4/16/32, `Boundary` =
  `Disjoint`/`Shared`/`NoCondition`). `Disjoint` is the one that matters: every writer owns a
  tag no other writer touches, so nothing in DCB semantics requires serializing them — but the
  single-tenant append lock is keyed `alberto-append:{schema}`, one key for the whole store,
  not per boundary. If `Disjoint` throughput does not scale with `Writers`, that is the lock,
  not genuine contention, and it is what a per-boundary locking redesign ("B1") would target.
  `Shared` is the control that should *not* scale — every writer targets the same tag, so real
  conflicts exist regardless of locking strategy — and it also answers the conflict-rate half
  of the question: BenchmarkDotNet has no column for a second metric, so `Shared` prints
  `[conflict-rate] Writers=… successes=… conflicts=… rate=…` to stdout on cleanup; grep for it.
  `NoCondition` isolates the lock+insert cost from the conflict-check `SELECT` the other two
  also pay (no `DcbQuery` at all).
- **`TenantConcurrentAppendBenchmarks`** — the multi-tenant control for the same question.
  `[Params] Writers`, `DifferentTenants` (bool). The multi-tenant lock key is
  `alberto-append:{schema}:{tenantId}`, so `DifferentTenants=true` gives every writer its own
  lock while `false` puts them all on one, mirroring `Disjoint` above but with a lock that
  genuinely differs per writer. No `.WithTenancy()` module/DI setup — deliberately the cheapest
  version: it constructs `PostgresTenantEventStoreBackend` directly against a freshly migrated,
  unseeded multi-tenant database (see `BenchmarkDatabase.CreateFreshDatabaseAsync`).
- **`AppendWithDcbCheckStoreSizeBenchmarks`** — `AppendWithDcbCheck` at 10k/100k/1M
  (`[Params] StoreSize`). [results.md](../docs/benchmarks/results.md#appends) flags "appends
  carry no store-size axis" as untested for the one append case that actually reads (the DCB
  conflict-check scan runs under the append lock); this is that axis, on its own class so it
  doesn't triple `AppendBenchmarks`' two cases that provably don't read. Queries a tag with
  real accumulated history (`"order":"1"`), not an absent one, but expect the mean to stay flat
  across StoreSize regardless: `alberto_append_events`'s conflict check is `tag = ANY(...) AND
  global_position > expected_position LIMIT 1` against the `(tag, global_position)` primary
  key, so it is always a seek to the tail of one tag's range, never a scan over its
  accumulated history — an O(log n) index descent in table size either way. A flat result here
  is itself the answer to "does the check get slower as the store grows", not evidence the
  benchmark isn't exercising real history.

Every class above uses `OperationsPerInvoke` on a *fixed* total-appends constant (256), split
evenly across `Writers` — not `Writers`-scaled — because `OperationsPerInvoke` has to be a
compile-time constant and can't vary with a `[Params]` value. The same amount of work divided
by the same constant at every `Writers` value is what makes the reported mean a genuine
per-append time rather than a per-writer-round time.

### Experimental ceiling: B1's upper bound

`benchmarks/experiments/no-append-lock.patch` comments out the append lock acquisition
entirely, to show what `Disjoint` could reach if per-boundary locking existed. It is not a
runtime flag and must never become one — an env var in the lock-acquisition path is a
lock-free path production code could hit by accident; a patch that has to be applied by hand to
a throwaway tree cannot be. **Never commit it applied.**

It is not a true lock-free ceiling: every append function calls `PERFORM pg_notify(...)`
(`004_StructuredConflictPosition.sql`), and committing backends that queue a notification
serialize against each other at commit — see the note at
`Migrations/001_InitialSchema.sql:355` ("each NOTIFY locked the cluster-wide notification
queue on commit"), made about the checkpoint/dead-letter notify triggers 032 went on to drop.
The patch removes the advisory lock only — read the result as "without the advisory lock,
commit-time NOTIFY serialization remains", not as the true unbounded ceiling. The same NOTIFY
serialization applies to `TenantConcurrentAppendBenchmarks` too: `DifferentTenants=true` removes
the advisory lock's contribution by giving every writer its own key, but every writer still
commits through the same NOTIFY path, so it isolates the advisory lock and nothing else.

    git apply benchmarks/experiments/no-append-lock.patch
    dotnet build -c Release benchmarks/Alberto.Benchmarks
    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- \
      --filter '*ConcurrentAppendBenchmarks*Disjoint*'
    git apply -R benchmarks/experiments/no-append-lock.patch

At `Writers=32` there is also a client/VM-side ceiling to rule out before crediting the
advisory lock: 32 concurrent writers competing for CPU on a machine with fewer logical cores,
or Postgres running inside a Docker Desktop VM, can plateau on the client thread pool or the
VM's network stack before the lock itself does. If `Disjoint` throughput flattens at high
`Writers`, check host core count and Docker networking before reading that as the lock.

## Where this runs

Benchmarks execute only in `.github/workflows/benchmarks.yml`: nightly at 02:00 UTC, or on
manual `workflow_dispatch`. PR and push CI does not build or run this project at all, so a
refactor that breaks the runner surfaces on the next nightly run rather than on the PR. Build
it locally when you touch it, or dispatch the Benchmarks workflow from your branch.
