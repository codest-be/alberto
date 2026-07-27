# Benchmark results

Every number here comes from `benchmarks/results/local-3575380c/baseline.json`, measured on
one machine in one sitting. How to run and compare: [benchmarks/README.md](../../benchmarks/README.md).
Why the suite is shaped this way:
[the design doc](../superpowers/specs/2026-07-26-benchmark-suite-design.md).

## What was measured

| | |
|---|---|
| Machine | macOS, Arm64, 14 logical cores, 48 GiB, .NET 10.0.8 |
| Database | `postgres:16-alpine` in a Testcontainer |
| Harness | BenchmarkDotNet, `RunStrategy.Monitoring`, 10 iterations per case |
| Warm-up | Each case drives its own measured method 2000× (max 15s) before timing |
| Store sizes | 10k / 100k / 1M events, cloned from a seeded template per iteration |
| Seed shape | 3 event types uniformly, 100 distinct order tags, 1 tag per event |
| Page size | 500 events |

Read this as a **relative** instrument, not a datasheet. Postgres runs in Docker Desktop's
VM, where `fsync` is slower than on real hardware, so a ~1 ms single append says more about
the round trip than about Alberto. The suite exists to catch a change that makes something
20% worse, and to show which costs scale with the log and which do not.

Two things it does **not** yet measure: concurrency (every case is single-threaded on one
connection, so ops/sec is a latency reciprocal, not a throughput ceiling) and any other event
store. Both are later phases.

## Appends

Appends carry no store-size axis. That is a deliberate design assumption — writing does not
read, so table size should not change the answer — and it is worth naming as an assumption,
because the suite does not test it. Index maintenance on a 1M-row table is not obviously free.
Adding a store-size axis to one append case would settle it cheaply.

| Case | Mean | ±sd | Allocated |
|---|---:|---:|---:|
| `SingleAppend` | 912 µs | 5.7% | 11.5 KB |
| `AppendWithDcbCheck` | 945 µs | 5.6% | 12.0 KB |
| `AppendWithConflictDetected` | 792 µs | 10.8% | 21.1 KB |

**The DCB consistency check is not a meaningful tax.** 912 µs against 945 µs is +3.6%, well
inside the two cases' standard deviations. Appending with a consistency boundary costs about
the same as appending without one. That is the important result: DCB's whole premise is that
the boundary is checked in the same round trip as the write, and the measurement is consistent
with that.

The conflict path is *cheaper* than the success path, which is the right shape — a detected
conflict aborts before doing the insert work. Its higher allocation (21.1 KB vs 12.0 KB) is
the exception object and its message.

### Batching

| Batch size | Mean | Per event | Allocated | Per event |
|---:|---:|---:|---:|---:|
| 1 (`SingleAppend`) | 912 µs | 912 µs | 11.5 KB | 11.5 KB |
| 10 | 1550 µs | 155 µs | 28.4 KB | 2.8 KB |
| 100 | 4449 µs | 44.5 µs | 197.0 KB | 2.0 KB |
| 1000 | 29567 µs | 29.6 µs | 1914.1 KB | 1.9 KB |

**This is the single biggest operational lever in the suite.** Batching 1000 events costs
29.6 µs each against 912 µs each one at a time — a 31× efficiency gain — and the per-event
allocation flattens at about 1.9 KB. The curve is steeply concave: batches of 10 already
recover 6× of that, and 100 recovers 20×. Most of the win is available well before you need
1000-event batches, which matters because a large batch is one transaction holding locks for
30 ms.

Read the other direction, this quantifies the cost of *not* batching. A reactor that appends
per event is paying a round trip and an fsync per event, and nothing in Alberto can recover
that for it.

### Tag fan-out

| Tags per event | Mean | Allocated |
|---:|---:|---:|
| 1 | 924 µs | 11.5 KB |
| 5 | 1019 µs | 16.8 KB |
| 20 | 1152 µs | 19.7 KB |

**Tag fan-out is close to free.** Twenty times the tags costs 25% more time. Each tag writes
a row into `alberto_event_tag_positions`, so the write amplification is real but it rides
inside a transaction that has already paid for its round trip and fsync. Nothing here argues
for rationing tags on an event — model the domain, not the index.

## Reads

All reads return a 500-event page (or fewer, where the query is selective).

| Case | 10k | 100k | 1M | Shape |
|---|---:|---:|---:|---|
| `GetLastPosition` | 237 µs | 248 µs | 286 µs | flat |
| `GetStableHead` | 351 µs | 313 µs | 361 µs | flat |
| `BoundaryRead` | 420 µs | 524 µs | 474 µs | flat |
| `StreamAllFromZero` | 698 µs | 745 µs | 712 µs | flat |
| `TailRead` | 682 µs | 695 µs | 682 µs | flat |
| `StreamByTag` | 624 µs | 1272 µs | 1366 µs | grows, then flat |
| `StreamByType` | 1267 µs | 2330 µs | 2606 µs | grows, then flat |
| `StreamByTypeAndTag` | 630 µs | 2672 µs | 6042 µs | **grows** |
| `StreamByMultiTag` (2 tags) | 759 µs | 1640 µs | 1549 µs | grows, then flat |
| `StreamByMultiTag` (8 tags) | 1309 µs | 2119 µs | 2160 µs | grows, then flat |

**The headline is that the unfiltered reads are flat across a 100× growth in the log.** That
is the property an event store lives or dies by: a paged read should cost what it *returns*,
not what the store *holds*. `StreamAllFromZero` and `TailRead` sit within 10% of themselves at
every size, and position lookups allocate nothing at all and answer in ~0.3 ms throughout.

**The filtered reads step up from 10k to 100k, then flatten.** For three of them the step is
not a scaling property at all but a smaller answer, and the allocation column proves it: the
seed carries only 100 distinct order tags, so at 10k a single-tag query has ~100 matching
events in the entire store and cannot fill a 500-event page. `StreamByTag` allocates 73.7 KB
at 10k against 334 KB at both larger sizes, `StreamByTypeAndTag` 30.3 KB against 221/332 KB,
and `StreamByMultiTag` (2 tags) 133 KB against 322 KB. Those 10k numbers are cheaper because
they return less, not because the store is smaller. Compare like-for-like at 100k → 1M, where
each returns a full page, and the growth is 7%, 126% and −6% respectively.

**`StreamByType` and `StreamByMultiTag` (8 tags) return a full 500-event page at every size**
— allocations are identical to within 0.1% across the sweep — so their 10k → 100k step is
real: 1267 → 2330 µs (+84%) and 1309 → 2119 µs (+62%). Both then flatten, +12% and +2% over
the next 10×. The flattening is the important half, and it is the shape you want. The step
itself is not fully explained by anything measured here: a type predicate matches a third of
the log, so the 500th match lives around position 1500 no matter how large the store is, and
the scan should not care. The likeliest explanation is that a 10k store (~3.6 MB) is entirely
resident in shared buffers while a 100k one is not, but the suite does not measure buffer hit
rates, so that stays a hypothesis rather than a finding.

**`StreamByTypeAndTag` is the one read that genuinely scales with the store**, and it is worth
being precise about what changed. Before the fix in this branch it read 1213 / 4052 / 34776 µs
— a 29× jump for a 10× growth between 100k and 1M, because the query used `INTERSECT` and a
`LIMIT` cannot push through a set operation, so both branches materialised in full before a
single row was discarded. It now reads 630 / 2672 / 6042 µs. The cliff is gone (−82.6% at 1M),
but 2672 → 6042 µs is still a 2.3× cost for a 10× store, and that remains the most interesting
open question in the suite.

This case is also the one whose plan choice is worth knowing about. plpgsql plans a statement
against actual parameter values for roughly five calls and may then switch to a value-agnostic
*generic* plan for the rest of the session. Pricing both halves with `plan_cache_mode` shows
this query's generic plan (1.13 ms) is much faster than its custom plan (2.68 ms), so early
calls on a pooled connection are the slow ones. The numbers above are all generic-plan numbers
— the harness now drives each query thousands of times before measuring, which is what a
long-lived connection sees, and it is why the 100k entry no longer carries the 60% standard
deviation it did when the measurement straddled both plans. The same mechanism running the
*other* way is what made an earlier draft of the fix 37% slower at 100k despite a perfectly
good custom plan — see the comment block in
[migration 028](../../src/Alberto.Dcb.Postgres/Migrations/028_SemiJoinTypesAndTagsRead.sql).

**Tag unions are cheaper than they look.** Eight tags cost roughly 1.3× two tags, not 4×, and
neither grows with the store. The union is served by one index range scan per tag against
`(tag, global_position)`, so adding tags adds scans over already-ordered data rather than
multiplying work.

### Allocations

A 500-event page allocates ~320 KB, about 640 bytes per event, consistently across every read
family and every store size. `GetLastPosition` and `GetStableHead` allocate zero. Both are
what you want: paging cost is proportional to the page, and the polling loop's cheapest
question is free.

## Why the harness warms up before it measures

An earlier version of these results had the `StoreSize=10000` column reading as the *slowest*
in several unrelated read benchmarks — `StreamAllFromZero` at 1687 / 962 / 780 µs and
`TailRead` at 1543 / 896 / 802 µs. Store size cannot make a `LIMIT 500` read slower, so that
was the harness, not Alberto. It is fixed, and the fix is worth knowing about because it
determines what these numbers mean.

BenchmarkDotNet runs every case — including every `[Params]` combination — in its own process,
and each process seeds its own store. So a case parameterised at 1M events has pushed a
million rows through Npgsql before it measures anything, while the 10k case has pushed ten
thousand. The 1M process arrived at measurement deeply warm and the 10k one arrived cold, and
BenchmarkDotNet's two warmup iterations are nowhere near enough to close that: tiered JIT
promotes a method only after ~30 calls. Each case now drives its own measured method up to
2000 times, or 15 seconds, in `[GlobalSetup]` before timing starts — see
[Warmup.cs](../../benchmarks/Alberto.Dcb.Benchmarks/Harness/Warmup.cs), which records what was
measured at 30, 300 and 2000 invocations and why the number is where it is.

Two consequences for reading the tables above. Every 10k case got 39–59% faster and the
smallest store is now the fastest, which is the only shape the data can justify. And the
numbers are steady-state, warm-connection numbers throughout — that is deliberate, since it
is what a long-lived pooled connection in a running service sees, but it means they do not
describe the first few calls after a cold start, which are slower.

## Caveats

- Single-threaded, one connection. Nothing here measures contention, lock waits, or how the
  store behaves with many concurrent writers.
- 10 iterations per case. Enough to catch a 20% regression, not enough to resolve a 10%
  difference — several comparisons above are explicitly left unresolved for this reason.
- Postgres in a Docker VM on macOS. Absolute latencies are not production latencies.
- One machine profile. Results are keyed by machine, and the comparer refuses to diff across
  profiles rather than warning, so these numbers say nothing about CI's hardware.
- No comparison against other event stores yet.
