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
| `SingleAppend` | 882 µs | 8.1% | 11.5 KB |
| `AppendWithDcbCheck` | 950 µs | 9.7% | 12.0 KB |
| `AppendWithConflictDetected` | 694 µs | 6.9% | 21.6 KB |

**The DCB consistency check is not a meaningful tax.** 882 µs against 950 µs is +7.7%, inside
the two cases' standard deviations. Appending with a consistency boundary costs about the same
as appending without one. That is the important result: DCB's whole premise is that the
boundary is checked in the same round trip as the write, and the measurement is consistent
with that.

The conflict path is *cheaper* than the success path, which is the right shape — a detected
conflict aborts before doing the insert work. Its higher allocation (21.6 KB vs 12.0 KB) is
the exception object and its message.

### Batching

| Batch size | Mean | Per event | Allocated | Per event |
|---:|---:|---:|---:|---:|
| 1 (`SingleAppend`) | 882 µs | 882 µs | 11.5 KB | 11.5 KB |
| 10 | 1631 µs | 163 µs | 28.4 KB | 2.8 KB |
| 100 | 4263 µs | 42.6 µs | 197.0 KB | 2.0 KB |
| 1000 | 29572 µs | 29.6 µs | 1913.5 KB | 1.9 KB |

**This is the single biggest operational lever in the suite.** Batching 1000 events costs
29.6 µs each against 882 µs each one at a time — a 30× efficiency gain — and the per-event
allocation flattens at about 1.9 KB. The curve is steeply concave: batches of 10 already
recover 5× of that, and 100 recovers 21×. Most of the win is available well before you need
1000-event batches, which matters because a large batch is one transaction holding locks for
30 ms.

Read the other direction, this quantifies the cost of *not* batching. A reactor that appends
per event is paying a round trip and an fsync per event, and nothing in Alberto can recover
that for it.

### Tag fan-out

| Tags per event | Mean | Allocated |
|---:|---:|---:|
| 1 | 880 µs | 11.5 KB |
| 5 | 1053 µs | 16.8 KB |
| 20 | 1569 µs | 20.0 KB |

**Tag fan-out is cheap, but this is the one row in the suite whose number should not be quoted
to three digits.** The 20-tag case is bimodal across runs — four measurements of the same code
gave 1086, 1152, 1569 and 1602 µs, in two tight clusters rather than a spread (see
[Run-to-run bimodality](#run-to-run-bimodality)). So twenty times the tags costs somewhere
between +25% and +80%, and the suite as it stands cannot narrow that further.

The qualitative reading survives the uncertainty at either end. Each tag writes a row into
`alberto_event_tag_positions`, so the write amplification is real, but 20× the index rows
costs well under 2× the time because it rides inside a transaction that has already paid for
its round trip and fsync. Nothing here argues for rationing tags on an event — model the
domain, not the index.

## Reads

All reads return a 500-event page (or fewer, where the query is selective).

| Case | 10k | 100k | 1M | Shape |
|---|---:|---:|---:|---|
| `GetLastPosition` | 362 µs | 298 µs | 253 µs | flat |
| `GetStableHead` | 412 µs | 337 µs | 310 µs | flat |
| `BoundaryRead` | 500 µs | 432 µs | 417 µs | flat |
| `StreamAllFromZero` | 681 µs | 689 µs | 673 µs | flat |
| `TailRead` | 688 µs | 654 µs | 677 µs | flat |
| `StreamByTag` | 772 µs | 1372 µs | 1274 µs | grows, then flat |
| `StreamByType` | 1391 µs | 1997 µs | 2436 µs | grows, then flat |
| `StreamByTypeAndTag` | 588 µs | 2676 µs | 3205 µs | grows, then flat |
| `StreamByMultiTag` (2 tags) | 686 µs | 1383 µs | 1380 µs | grows, then flat |
| `StreamByMultiTag` (8 tags) | 1068 µs | 1964 µs | 2040 µs | grows, then flat |

**The headline is that the unfiltered reads are flat across a 100× growth in the log.** That
is the property an event store lives or dies by: a paged read should cost what it *returns*,
not what the store *holds*. `StreamAllFromZero` and `TailRead` sit within 10% of themselves at
every size, and position lookups allocate nothing at all and answer in a few hundred
microseconds throughout.

**Where a filtered read looks cheap, check whether it returned less.** The seed carries only
100 distinct order tags, so at 10k a single-tag query has ~100 matching events in the entire
store and cannot fill a 500-event page. The allocation column shows this directly, since a
full page costs ~322 KB: `StreamByTag` allocates 72 KB at 10k against 328/327 KB at the larger
sizes, `StreamByMultiTag` (2 tags) 133 KB against 321/321 KB, and `StreamByTypeAndTag` 30 KB
at 10k and still only 216 KB at 100k, reaching a full page only at 1M. Those small numbers are
cheap because they return less, not because the store is smaller. Compare like-for-like — both
ends returning a full page — and `StreamByTag` goes 1372 → 1274 µs (−7%) and `StreamByMultiTag`
(2 tags) 1383 → 1380 µs (0%) over the last 10×.

**`StreamByType` and `StreamByMultiTag` (8 tags) return a full 500-event page at every size**
— allocations are identical to within 0.1% across the sweep — so their 10k → 100k step is
real: 1391 → 1997 µs (+44%) and 1068 → 1964 µs (+84%). Both then flatten, +22% and +4% over
the next 10×. The flattening is the important half, and it is the shape you want. The step
itself is not fully explained by anything measured here: a type predicate matches a third of
the log, so the 500th match lives around position 1500 no matter how large the store is, and
the scan should not care. The likeliest explanation is that a 10k store (~3.6 MB) is entirely
resident in shared buffers while a 100k one is not, but the suite does not measure buffer hit
rates, so that stays a hypothesis rather than a finding.

**`StreamByTypeAndTag` was the one read that genuinely scaled with the store, and it no longer
does.** It took two fixes, and the sequence is worth keeping because each one exposed the next.

Originally it read 1213 / 4052 / 34776 µs — a 29× jump for a 10× growth — because the query
used `INTERSECT`, and a `LIMIT` cannot push through a set operation, so both branches
materialised in full before a single row was discarded. Rewriting it as a semi-join
([migration 028](../../src/Alberto.Dcb.Postgres/Migrations/028_SemiJoinTypesAndTagsRead.sql))
took 1M to 6042 µs, −82.6%. That killed the cliff but left a 2.3× cost for a 10× store.

The remainder was a blocking `Sort`. Both predicates arrived as `= ANY($array)`, which is
opaque to the planner: it cannot know the array holds one element, so it cannot see that a
scan of the `(tenant_id, tag, global_position)` primary key is already in position order, and
it inserted a `Sort → Unique` above the scan to guarantee the ordering. A `Sort` is a blocking
node, so `LIMIT 500` could not terminate the scan early — every matching position in the store
was read and sorted before 500 were kept.
[Migration 029](../../src/Alberto.Dcb.Postgres/Migrations/029_ScalarFastPathTypesAndTagsRead.sql)
adds a fast path for the single-tag/single-type case that compares scalars instead, so the
index order is visible to the planner, the `Sort` disappears, and the scan stops as soon as the
page is full: **6042 → 3205 µs at 1M, −47%**, at unchanged allocations.

That the 100k column did not move (2672 → 2676 µs) is the same mechanism seen from the other
side, and is the best evidence the explanation is right. At 100k a single tag matches ~1,000
events, of which ~1/3 also match the type — about 335, below the page size. The allocation
column confirms it: 216 KB, not a full page. There is no early termination to win when the
query has to exhaust its axis anyway.

What remains is ordinary shape rather than a scaling defect: 588 / 2676 / 3205 µs, where the
first two columns return partial pages and the last is a full one. It is still ~4.8× the cost
of `StreamAllFromZero` for an identically sized page, which is the price of intersecting two
index axes rather than walking one.

This case is also the one whose plan choice is worth knowing about. plpgsql plans a statement
against actual parameter values for roughly five calls and may then switch to a value-agnostic
*generic* plan for the rest of the session. Under connection pooling the generic plan is what
almost every real call gets, so a candidate must be judged on its generic plan — pricing both
halves with `SET LOCAL plan_cache_mode` is how migrations 028 and 029 were both chosen. The
numbers above are all generic-plan numbers — the harness drives each query thousands of times
before measuring, which is what a long-lived connection sees, and it is why the 100k entry no
longer carries the 60% standard deviation it did when the measurement straddled both plans.
Absolute plan-level timings are not quoted here because they were measured on a hand-driven
connection rather than through the suite, and they did not transfer: they under-predicted the
benchmark's own 1M figure by ~3×, which is why the 029 win was verified by re-running the
suite rather than by trusting the SQL-level measurement. The same mechanism running the
*other* way is what made an earlier draft of the fix 37% slower at 100k despite a perfectly
good custom plan — see the comment block in
[migration 028](../../src/Alberto.Dcb.Postgres/Migrations/028_SemiJoinTypesAndTagsRead.sql).

**Tag unions are cheaper than they look.** Eight tags cost roughly 1.5× two tags, not 4×, and
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

Two consequences for reading the tables above. Every 10k case got 39–59% faster, and the
inverted ordering is gone: no read is now systematically slower at 10k than at 1M in a way
store size could not explain. (Three flat cases — `GetLastPosition`, `GetStableHead`,
`BoundaryRead` — still read highest at 10k in the committed baseline, but the differences are
tens of microseconds on sub-millisecond cases and fall inside the mode gap described under
[Run-to-run bimodality](#run-to-run-bimodality).) And the
numbers are steady-state, warm-connection numbers throughout — that is deliberate, since it
is what a long-lived pooled connection in a running service sees, but it means they do not
describe the first few calls after a cold start, which are slower.

## Run-to-run bimodality

Validating migration 029 meant running the full suite four times against code that could only
affect one case. The other cases were supposed to be the control group, and they were not
quiet: each run flagged two or three unrelated cases as regressed or improved by more than the
20% gate, but never the same ones.

Extracting the flagged cases across all four runs shows why, and it is not ordinary noise:

| Case | Run 1 | Run 2 | Run 3 | Run 4 |
|---|---:|---:|---:|---:|
| `GetLastPosition` (10k) | 237 µs | 360 µs | 232 µs | 362 µs |
| `AppendWithTagFanOut` (20 tags) | 1152 µs | 1602 µs | 1086 µs | 1569 µs |
| `TailRead` (1M) | 682 µs | 815 µs | 897 µs | 677 µs |

Those are two tight clusters per case, not a spread — `GetLastPosition` sits at ~235 or ~361
with nothing in between. Within any single run the case is stable (standard deviation as low
as 13 µs on a 232 µs mean), so the ten iterations agree with each other; it is the *run* that
lands in one mode or the other and stays there. The likely culprits are per-process
environmental state — container placement, CPU frequency, page-cache warmth — but the suite
does not measure any of them, so the mechanism is unidentified.

Three consequences, in order of how much they should change your reading:

1. **The short cases carry a ~50% run-to-run mode gap, which is larger than the 20% regression
   gate.** For `GetLastPosition` (10k), `AppendWithTagFanOut` (20 tags) and `TailRead` (1M),
   the gate cannot currently distinguish a real regression from a mode flip.
2. **The committed baseline holds high-mode values for `GetLastPosition` (10k) and
   `AppendWithTagFanOut` (20 tags).** That is the deliberate choice: a high-mode baseline
   under-detects regressions on those cases but never false-alarms, while a low-mode baseline
   would fail the build on roughly every other run. A baseline must be one coherent run, so
   re-measured values cannot be spliced in to fix individual entries.
3. **It does not affect the conclusions above.** Every case that moves is short (under ~1.6 ms)
   and the affected rows are called out where they appear. The 029 result is 47% on a 6 ms
   case, reproduced in four independent measurements, and confirmed by an isolated re-run.

Raising `IterationCount` would not help — the modes are between runs, not within them. The fix
is to run each case in several processes and take the minimum, or to identify and pin whatever
environmental state differs. Neither is done.

## Caveats

- Single-threaded, one connection. Nothing here measures contention, lock waits, or how the
  store behaves with many concurrent writers.
- 10 iterations per case. Enough to catch a 20% regression, not enough to resolve a 10%
  difference — several comparisons above are explicitly left unresolved for this reason.
- Short cases are bimodal between runs; see [Run-to-run bimodality](#run-to-run-bimodality)
  for which rows this affects and why the gate cannot currently see through it.
- Postgres in a Docker VM on macOS. Absolute latencies are not production latencies.
- One machine profile. Results are keyed by machine, and the comparer refuses to diff across
  profiles rather than warning, so these numbers say nothing about CI's hardware.
- No comparison against other event stores yet.
