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
| `SingleAppend` | 1022 µs | 27.6% | 11.5 KB |
| `AppendWithDcbCheck` | 1150 µs | 15.9% | 12.0 KB |
| `AppendWithConflictDetected` | 883 µs | 13.6% | 23.6 KB |

**The DCB consistency check is not a meaningful tax.** 1022 µs against 1150 µs looks like
+12.5%, but with 27.6% and 15.9% standard deviations those intervals overlap heavily — the
honest statement is that appending with a consistency boundary costs about the same as
appending without one, and the suite cannot resolve a difference this small at 10 iterations.
That is the important result: DCB's whole premise is that the boundary is checked in the same
round trip as the write, and the measurement is consistent with that.

The conflict path is *cheaper* than the success path, which is the right shape — a detected
conflict aborts before doing the insert work. Its higher allocation (23.6 KB vs 12.0 KB) is
the exception object and its message.

### Batching

| Batch size | Mean | Per event | Allocated | Per event |
|---:|---:|---:|---:|---:|
| 1 (`SingleAppend`) | 1022 µs | 1022 µs | 11.5 KB | 11.5 KB |
| 10 | 1660 µs | 166 µs | 28.4 KB | 2.8 KB |
| 100 | 4583 µs | 45.8 µs | 197.0 KB | 2.0 KB |
| 1000 | 29718 µs | 29.7 µs | 1913.3 KB | 1.9 KB |

**This is the single biggest operational lever in the suite.** Batching 1000 events costs
29.7 µs each against 1022 µs each one at a time — a 34× efficiency gain — and the per-event
allocation flattens at about 1.9 KB. The curve is steeply concave: batches of 10 already
recover 6× of that, and 100 recovers 22×. Most of the win is available well before you need
1000-event batches, which matters because a large batch is one transaction holding locks for
30 ms.

Read the other direction, this quantifies the cost of *not* batching. A reactor that appends
per event is paying a round trip and an fsync per event, and nothing in Alberto can recover
that for it.

### Tag fan-out

| Tags per event | Mean | Allocated |
|---:|---:|---:|
| 1 | 869 µs | 11.5 KB |
| 5 | 988 µs | 16.8 KB |
| 20 | 1126 µs | 19.7 KB |

**Tag fan-out is close to free.** Twenty times the tags costs 30% more time. Each tag writes
a row into `alberto_event_tag_positions`, so the write amplification is real but it rides
inside a transaction that has already paid for its round trip and fsync. Nothing here argues
for rationing tags on an event — model the domain, not the index.

## Reads

All reads return a 500-event page (or fewer, where the query is selective).

| Case | 10k | 100k | 1M | Shape |
|---|---:|---:|---:|---|
| `GetLastPosition` | 391 µs | 365 µs | 340 µs | flat |
| `GetStableHead` | 491 µs | 434 µs | 417 µs | flat |
| `BoundaryRead` | 908 µs | 573 µs | 557 µs | flat |
| `StreamAllFromZero` | 1687 µs | 962 µs | 780 µs | flat |
| `TailRead` | 1543 µs | 896 µs | 802 µs | flat |
| `StreamByTag` | 1266 µs | 1267 µs | 1328 µs | flat |
| `StreamByType` | 2089 µs | 2369 µs | 2463 µs | +18% over 100× |
| `StreamByTypeAndTag` | 1062 µs | 3778 µs | 5259 µs | **grows** |
| `StreamByMultiTag` (2 tags) | 1406 µs | 1629 µs | 1528 µs | flat |
| `StreamByMultiTag` (8 tags) | 2486 µs | 1932 µs | 2200 µs | flat |

**The headline is that almost everything is flat across a 100× growth in the log.** That is
the property an event store lives or dies by: a paged read should cost what it *returns*, not
what the store *holds*. Position lookups allocate nothing at all and answer in ~0.4 ms at
every size.

**`StreamByType` grows mildly** — 18% across 100× the data. With only 3 event types in the
seed, a type predicate matches a third of the log, so this is the least selective query in the
suite and its index scan touches more heap pages as the store grows. An 18% slope over two
orders of magnitude is a good outcome for the worst-selectivity case, not a problem.

**`StreamByTypeAndTag` is the one read that still scales with the store**, and it is worth
being precise about what changed. Before the fix in this branch it read 1213 / 4052 / 34776 µs
— a 29× jump for a 10× growth between 100k and 1M, because the query used `INTERSECT` and a
`LIMIT` cannot push through a set operation, so both branches materialised in full before a
single row was discarded. It now reads 1062 / 3778 / 5259 µs. The cliff is gone (−84.9% at 1M),
but the slope is not flat, and that remains the most interesting open question in the suite.

The `StreamByTypeAndTag[100k]` entry carries a 60.4% standard deviation, which is not
measurement sloppiness but a real property worth knowing about. plpgsql plans a statement
against actual parameter values for roughly five calls and may then switch to a value-agnostic
*generic* plan for the rest of the session. Pricing both halves with `plan_cache_mode` shows
this query's generic plan (1.13 ms) is much faster than its custom plan (2.68 ms), so early
calls on a pooled connection are the slow ones and the distribution is genuinely bimodal. The
median, 2.53 ms, is what a long-lived connection actually sees. The same mechanism running the
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

## A harness artifact to be aware of

Across several *unrelated* read benchmarks the `StoreSize=10000` column is both the slowest
and the noisiest — `StreamAllFromZero` reads 1687 / 962 / 780 µs and `TailRead` 1543 / 896 /
802 µs, i.e. the smallest store is the *slowest*. Store size cannot make a `LIMIT 500` read
faster, so this is not a property of the data. The 10k parameter set runs first in
BenchmarkDotNet's sweep, and it appears to absorb warmup — JIT, connection pool fill, Postgres
buffer cache — that later parameter sets no longer pay.

Treat the 10k column as indicative rather than exact, and compare like-for-like against the
baseline's own 10k column, which is what the regression gate does. Making the sweep order
immaterial is worth doing; it is not done here.

## Caveats

- Single-threaded, one connection. Nothing here measures contention, lock waits, or how the
  store behaves with many concurrent writers.
- 10 iterations per case. Enough to catch a 20% regression, not enough to resolve a 10%
  difference — several comparisons above are explicitly left unresolved for this reason.
- Postgres in a Docker VM on macOS. Absolute latencies are not production latencies.
- One machine profile. Results are keyed by machine, and the comparer refuses to diff across
  profiles rather than warning, so these numbers say nothing about CI's hardware.
- No comparison against other event stores yet.
