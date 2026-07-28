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
| Seed shape | 20 event types uniformly, 100 distinct order tags, 1 tag per event |
| Page size | 500 events |

Read this as a **relative** instrument, not a datasheet. Postgres runs in Docker Desktop's
VM, where `fsync` is slower than on real hardware, so a ~1 ms single append says more about
the round trip than about Alberto. The suite exists to catch a change that makes something
20% worse, and to show which costs scale with the log and which do not.

Two things it does **not** yet measure: concurrency (every case is single-threaded on one
connection, so ops/sec is a latency reciprocal, not a throughput ceiling) and any other event
store. Both are later phases.

> **The seed changed, so the read numbers are not a continuing series.** Every read figure
> published before this revision was measured against a corpus of **three** event types. It now
> holds twenty. That was a deliberate change — with three types, a query naming three types
> selects the entire store, so the suite could not measure what filtering by type costs, only
> what a filter that matches everything costs. Twenty uniform types turn the named-type count
> into a selectivity knob: naming *k* of them selects *k*/20 of the log. Read cases moved as a
> result, some by a lot, and those moves are the corpus, not a regression. Append cases are
> unaffected — they never name a type. Where an older number is quoted below for a
> before/after, it says which corpus it came from.

## Appends

Appends carry no store-size axis. That is a deliberate design assumption — writing does not
read, so table size should not change the answer — and it is worth naming as an assumption,
because the suite does not test it. Index maintenance on a 1M-row table is not obviously free.
Adding a store-size axis to one append case would settle it cheaply.

| Case | Mean | ±sd | Allocated | Re-measured |
|---|---:|---:|---:|---:|
| `SingleAppend` | 987 µs | 18.3% | 11.5 KB | 948 µs |
| `AppendWithDcbCheck` | 1444 µs | 10.9% | 12.0 KB | 974 µs |
| `AppendWithConflictDetected` | 922 µs | 17.8% | 21.3 KB | 715 µs |

**This baseline caught the whole append family in its high mode, and the last column is why
that matters.** An isolated re-run of the same three cases against the same commit, minutes
later, returned the numbers on the right — `AppendWithDcbCheck` at 974 µs rather than 1444 µs.
Both runs are honest measurements of identical code; the run, not the code, moves between two
modes (see [Run-to-run bimodality](#run-to-run-bimodality)). The baseline keeps the high values
because a baseline must be one coherent run and cannot have individual entries spliced in, and
because a high baseline under-detects rather than false-alarms. **Read the right-hand column
for what appends cost, and the left for what the gate will compare against.**

**On the re-measured numbers, the DCB consistency check is not a meaningful tax.** 948 µs
against 974 µs is +2.8%, far inside both cases' standard deviations. Appending with a
consistency boundary costs about the same as appending without one. That is the important
result: DCB's whole premise is that the boundary is checked in the same round trip as the
write, and the measurement is consistent with that. It would be easy to read the baseline
column alone as +46% and conclude the opposite, which is exactly the trap the bimodality
section exists to flag.

The conflict path is *cheaper* than the success path in both runs, which is the right shape —
a detected conflict aborts before doing the insert work. Its higher allocation (21.3 KB vs
12.0 KB) is the exception object and its message.

### Batching

| Batch size | Mean | Per event | Allocated | Per event |
|---:|---:|---:|---:|---:|
| 1 (`SingleAppend`) | 987 µs | 987 µs | 11.5 KB | 11.5 KB |
| 10 | 1644 µs | 164 µs | 28.1 KB | 2.8 KB |
| 100 | 4277 µs | 42.8 µs | 197.6 KB | 2.0 KB |
| 1000 | 30055 µs | 30.1 µs | 1924.4 KB | 1.9 KB |

**This is the single biggest operational lever in the suite.** Batching 1000 events costs
30 µs each against ~950–990 µs each one at a time — call it a 30× efficiency gain — and the
per-event allocation flattens at about 1.9 KB. The curve is steeply concave: batches of 10
already recover 6× of that, and 100 recovers 23×. Most of the win is available well before you
need 1000-event batches, which matters because a large batch is one transaction holding locks
for 30 ms.

Read the other direction, this quantifies the cost of *not* batching. A reactor that appends
per event is paying a round trip and an fsync per event, and nothing in Alberto can recover
that for it.

### Tag fan-out

| Tags per event | Mean | Allocated |
|---:|---:|---:|
| 1 | 906 µs | 11.5 KB |
| 5 | 950 µs | 16.8 KB |
| 20 | 1226 µs | 20.0 KB |

**Tag fan-out is cheap, but this is the one row in the suite whose number should not be quoted
to three digits.** The 20-tag case moves between runs: five measurements of the same code have
now given 1086, 1152, 1226, 1569 and 1602 µs. A four-run view of this case previously looked
like two tight clusters; the fifth value sits between them, so that reading was too clean and
is withdrawn — what can be said is that the spread is wide and reproducible, not that it is
cleanly bimodal. Twenty times the tags costs somewhere between +20% and +80%, and the suite as
it stands cannot narrow that further.

The qualitative reading survives the uncertainty at either end. Each tag writes a row into
`alberto_event_tag_positions`, so the write amplification is real, but 20× the index rows
costs well under 2× the time because it rides inside a transaction that has already paid for
its round trip and fsync. Nothing here argues for rationing tags on an event — model the
domain, not the index.

## Reads

All reads return a 500-event page (or fewer, where the query is selective).

| Case | 10k | 100k | 1M | Shape |
|---|---:|---:|---:|---|
| `GetLastPosition` | 269 µs | 230 µs | 270 µs | flat |
| `GetStableHead` | 320 µs | 333 µs | 339 µs | flat |
| `BoundaryRead` | 448 µs | 459 µs | 462 µs | flat |
| `StreamAllFromZero` | 665 µs | 682 µs | 686 µs | flat |
| `TailRead` | 754 µs | 635 µs | 683 µs | flat |
| `StreamByTag` | 583 µs | 1253 µs | 1425 µs | grows, then flat |
| `StreamByTypeAndTag` (1 type) | 526 µs | 912 µs | 4829 µs | grows |
| `StreamByTypesAndTag` (3 types) | 540 µs | 1386 µs | 4681 µs | grows |
| `StreamByType` | 1494 µs | 1931 µs | 9332 µs | grows |
| `StreamByMultiTag` (2 tags) | 692 µs | 1470 µs | 1574 µs | grows, then flat |
| `StreamByMultiTag` (8 tags) | 1189 µs | 2286 µs | 2178 µs | grows, then flat |

**The headline is that the unfiltered reads are flat across a 100× growth in the log.** That
is the property an event store lives or dies by: a paged read should cost what it *returns*,
not what the store *holds*. `StreamAllFromZero` and `TailRead` sit within 10% of themselves at
every size, and position lookups allocate nothing at all and answer in a few hundred
microseconds throughout.

**Where a filtered read looks cheap, check whether it returned less** — and with twenty types
the allocation column now predicts exactly how much less. A full page costs ~320 KB, about
640 bytes per event, so allocations divide back into a row count. The seed holds 100 order
tags, so a single tag matches `storeSize/100` events, and naming *k* of twenty types keeps
*k*/20 of those:

| Case | 10k | 100k | 1M |
|---|---|---|---|
| `StreamByTypeAndTag` predicted | 5 | 50 | 500 (page-capped) |
| …allocated | 12.3 KB | 32.7 KB | 284 KB |
| `StreamByTypesAndTag` predicted | 15 | 150 | 1500 → 500 |
| …allocated | 15.7 KB | 96.2 KB | 335 KB |

96.2 KB is 150 events to within a rounding error, and 32.7 KB is 51. The arithmetic and the
measurement agree, which is the strongest evidence in this document that the seed does what it
claims. It also means **the two-axis cases are the only reads that reach a full page at 1M and
not before**, so their 10k and 100k columns are cheap for the boring reason.

**`StreamByType`, `StreamByTag` at 100k+ and `StreamByMultiTag` return a full page at every
size**, so their curves are like-for-like. `StreamByTag` goes 1253 → 1425 µs (+14%) over the
last 10×, and `StreamByMultiTag` 1470 → 1574 µs (+7%) at two tags and 2286 → 2178 µs (−5%) at
eight. Those are the flat-at-scale shapes you want.

**`StreamByType` is now the slowest read in the suite, and the corpus change is what made that
visible.** It reads 1494 / 1931 / **9332** µs; against the old three-type corpus the 1M column
was 2436 µs. Naming one of twenty types instead of one of three is the whole difference, and
investigating it turned up a real defect rather than just a harder workload. `EXPLAIN` on
`alberto_read_by_types` shows its generic plan never uses the
`(tenant_id, event_type, global_position)` index at all: it sequentially scans the whole of
`alberto_event_type_positions` — one row per event in the store — filters, sorts, and
merge-joins the result against `alberto_events` walked in position order. That is the same
`= ANY` opacity
pathology that migrations 028–030 removed from the two-axis read, still present here on the
one-axis read.

The corpus explains why it surfaced now. At 1M, `order-placed` is 50,133 of the million events
and the 500th one sits at global position 9,557; with three types the 500th would have sat
near 1,500. The merge join therefore walks ~6.4× more full `alberto_events` rows — each
carrying JSONB — to fill the same page, which is the right order of magnitude for
2436 → 9332 µs. Measured directly at 1M, one type, limit 500, wrapped in real plpgsql
functions: the shipped body takes **7.9 ms** (73 ms forced onto a generic plan) against
**0.25 ms** for a scalar single-type branch of the same shape as migration 029's. That fix is
not in this change — it is a different function and was outside the question this work set out
to settle — but it is a ~30× win sitting in plain sight, and it is filed as follow-up work
along with the three never-audited wildcard read functions.

**`StreamByTypeAndTag` was the one read that genuinely scaled with the store, and the fix
sequence is worth keeping because each step exposed the next.** All three of these figures come
from the old three-type corpus and are a self-consistent series:

| | 10k | 100k | 1M |
|---|---:|---:|---:|
| `INTERSECT` (original) | 1213 µs | 4052 µs | 34776 µs |
| [028](../../src/Alberto.Dcb.Postgres/Migrations/028_SemiJoinTypesAndTagsRead.sql) semi-join | — | 2672 µs | 6042 µs |
| [029](../../src/Alberto.Dcb.Postgres/Migrations/029_ScalarFastPathTypesAndTagsRead.sql) scalar fast path | 588 µs | 2676 µs | 3205 µs |

A 29× jump for a 10× growth was the starting point, because `INTERSECT` is a set operation and
a `LIMIT` cannot push through one — both branches materialised in full before a single row was
discarded. Rewriting it as a semi-join took 1M to 6042 µs, −82.6%, killing the cliff but
leaving a 2.3× cost for a 10× store.

The remainder was a blocking `Sort`. Both predicates arrived as `= ANY($array)`, which is
opaque to the planner: it cannot know the array holds one element, so it cannot see that a
scan of the `(tenant_id, tag, global_position)` primary key is already in position order, and
it inserted a `Sort → Unique` above the scan to guarantee the ordering. A `Sort` is a blocking
node, so `LIMIT 500` could not terminate the scan early — every matching position in the store
was read and sorted before 500 were kept. Migration 029 added a fast path for the
single-tag/single-type case that compares scalars instead, so the index order is visible to the
planner, the `Sort` disappears, and the scan stops as soon as the page is full: **6042 →
3205 µs at 1M, −47%**, at unchanged allocations.

That the 100k column did not move (2672 → 2676 µs) was the same mechanism seen from the other
side, and the best evidence the explanation was right: at 100k the query returned a partial
page, so there was no early termination to win.

On today's twenty-type corpus the same case reads **526 / 912 / 4829 µs**. The 1M column is
higher than 029's 3205 µs and that is not a regression — a tag∩type cell now holds ~500 events
instead of ~3300, so filling a 500-event page means traversing essentially the whole 10,000-row
tag range rather than the first sixth of it. Same code, harder question. The 100k column fell
by two thirds (2676 → 912 µs) for the mirror-image reason: it now returns ~50 events instead of
~335.

### Migration 030: what 029's evidence could not have shown

029 shipped its fast path behind a guard requiring **one tag and one type**. The wider guard —
one tag, any number of types — was considered and rejected on a measurement taken against the
three-type corpus, where the multi-type comparison case named three of three types. A predicate
that matches every row in the store cannot show what filtering costs, so that rejection rested
on evidence incapable of supporting it. Widening the corpus was the point of this change, and
`StreamByTypesAndTag` — one tag intersected with three of twenty types, deliberately just
outside 029's guard — is the case that settles it.

It settles it two ways, and the second was not anticipated.

**The remaining `Sort` does matter in practice, not only in principle.** On the twenty-type
corpus, before migration 030, that shape cost 675 / 2783 / **14731** µs — against 4829 µs for
its single-type sibling returning a comparable page at 1M. The general path was ~3× the cost of
the fast path for a query one type-name away from it.

**But the widening originally rejected is still not the fix.** Simply relaxing 029's guard to
`array_length(p_tags,1) = 1` while leaving `etp.event_type = ANY(p_types)` on the type-position
index barely beats the general path *and* makes the shipped one-type case ~2.3× worse: a scalar
probe into `(tenant_id, event_type, global_position)` is a single index descent, while the
array form costs one descent per element on every outer row and estimates badly. The type axis
wants a *different plan* than the tag axis, which is what 029's evidence — measured where the
type predicate was a no-op — had no way to reveal.

[Migration 030](../../src/Alberto.Dcb.Postgres/Migrations/030_ScalarTagFastPathTypesAndTagsRead.sql)
therefore adds a second branch rather than widening the first. One tag and one type keeps 029's
scalar probe. One tag and several types drops the type-position index entirely and tests
`e.event_type = ANY(p_types)` on the `alberto_events` row the query has to fetch anyway,
trading an index probe per candidate for a heap fetch per candidate — which loses at one type
and wins from two upward. Two or more tags still falls through to the general path, where the
scalar rewrite is unavailable because more than one tag is precisely what the array parameter
exists to express.

Candidate timings, as plpgsql functions under `SET plan_cache_mode = force_generic_plan`, min
of 12 warm calls, twenty-type corpus, one tag, limit 500 (ms):

| | 1M / 1 type | 1M / 3 types | 1M / 10 types | 100k / 3 types |
|---|---:|---:|---:|---:|
| 028 general path | 8.975 | 9.213 | 7.845 | 2.219 |
| 029 relaxed to `= ANY` | 8.850 | 8.866 | — | 2.185 |
| type tested on the events row | 7.648 | **2.561** | **0.799** | **0.636** |
| 029 scalar type | **3.202** | n/a | n/a | n/a |

Through the suite, migration 030 moves `StreamByTypesAndTag` from 675 / 2783 / 14731 µs to
**540 / 1386 / 4681 µs** — −20% / −50% / **−68%** — and leaves the single-type sibling
untouched. At 1M the two now cost the same (4681 vs 4829 µs) for the same 500-event page, which
is the result worth having: a boundary that names three of a context's event types is no longer
penalised for not naming exactly one.

Neither branch needs a `DISTINCT`. An event carries exactly one `event_type`, so testing it —
on the type-position primary key or on the events row — cannot make a position match twice, and
a single scalar tag yields at most one tag-position row per position. No result can consume two
slots of `p_limit`.

**Tag unions are cheaper than they look.** Eight tags cost roughly 1.5× two tags, not 4×, and
neither grows with the store. The union is served by one index range scan per tag against
`(tag, global_position)`, so adding tags adds scans over already-ordered data rather than
multiplying work.

### Plan choice, and why SQL-level timings are quoted separately

plpgsql plans a statement against actual parameter values for roughly five calls and may then
switch to a value-agnostic *generic* plan for the rest of the session. Under connection pooling
the generic plan is what almost every real call gets, so a candidate must be judged on its
generic plan — pricing both halves with `plan_cache_mode` is how migrations 028, 029 and 030
were all chosen. The suite's numbers are all generic-plan numbers, since the harness drives
each query thousands of times before measuring, which is what a long-lived connection sees.

Two traps, both hit during this work and both worth inheriting:

- **SQL-level timings do not transfer to suite timings.** They under-predicted the benchmark's
  own 1M figure by ~3× for 029, which is why every win here is verified by re-running the suite
  rather than by trusting a hand-driven `psql` measurement.
- **A set-returning plpgsql function cannot use a parallel plan, but a standalone
  `PREPARE`/`EXECUTE` of the same SQL can.** `EXPLAIN` on the bare statement will happily show
  you a `Gather Merge` the shipped function will never get. Every candidate above was therefore
  wrapped in an actual plpgsql function before being timed. The same mechanism running the
  *other* way is what made an earlier draft of 028 37% slower at 100k despite a perfectly good
  custom plan — see that migration's comment block.

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
store size could not explain. And the numbers are steady-state, warm-connection numbers
throughout — that is deliberate, since it is what a long-lived pooled connection in a running
service sees, but it means they do not describe the first few calls after a cold start, which
are slower.

## Run-to-run bimodality

Validating migration 029 meant running the full suite four times against code that could only
affect one case. The other cases were supposed to be the control group, and they were not
quiet: each run flagged two or three unrelated cases as regressed or improved by more than the
20% gate, but never the same ones.

Extracting the flagged cases across those four runs showed why, and it is not ordinary noise:

| Case | Run 1 | Run 2 | Run 3 | Run 4 |
|---|---:|---:|---:|---:|
| `GetLastPosition` (10k) | 237 µs | 360 µs | 232 µs | 362 µs |
| `AppendWithTagFanOut` (20 tags) | 1152 µs | 1602 µs | 1086 µs | 1569 µs |
| `TailRead` (1M) | 682 µs | 815 µs | 897 µs | 677 µs |

Those looked like two tight clusters per case rather than a spread — `GetLastPosition` at ~235
or ~361 with nothing in between. Within any single run the case is stable (standard deviation
as low as 13 µs on a 232 µs mean), so the ten iterations agree with each other; it is the *run*
that lands in a mode and stays there.

**Two later observations qualify that picture and neither is comfortable.** A fifth run put
`AppendWithTagFanOut` (20 tags) at 1226 µs — between the two supposed clusters — so "cleanly
bimodal" was an over-reading of four points, and the honest statement is that the spread is
wide and reproducible with structure that is not established. And the run promoted as the
current baseline caught the entire *append family* high at once (`AppendWithDcbCheck` 1444 µs
against 974 µs on an isolated re-run of the same commit), which is a larger swing than any
previously catalogued case and affects the family whose headline conclusion is the DCB check's
cost. The likely culprits remain per-process environmental state — container placement, CPU
frequency, page-cache warmth — but the suite measures none of them, so the mechanism is still
unidentified.

Three consequences, in order of how much they should change your reading:

1. **Short cases carry a run-to-run mode gap larger than the 20% regression gate.** For
   `GetLastPosition` (10k), `AppendWithTagFanOut` (20 tags), `TailRead` (1M) and the whole
   append family, the gate cannot currently distinguish a real regression from a mode flip.
2. **The committed baseline holds high-mode values for those cases**, deliberately: a high
   baseline under-detects regressions there but never false-alarms, while a low baseline would
   fail the build on roughly every other run. A baseline must be one coherent run, so
   re-measured values cannot be spliced in to fix individual entries — which is why the Appends
   table above carries the re-measurement in its own column instead.
3. **It does not affect the conclusions above.** Every affected case is short (under ~1.6 ms),
   the affected rows are called out where they appear, and the two structural results — 029's
   47% and 030's 68% — are on multi-millisecond cases, reproduced across independent runs, and
   corroborated by SQL-level measurement of the specific plans involved.

Raising `IterationCount` would not help — the modes are between runs, not within them. The fix
is to run each case in several processes and take the minimum, or to identify and pin whatever
environmental state differs. Neither is done.

## Caveats

- Single-threaded, one connection. Nothing here measures contention, lock waits, or how the
  store behaves with many concurrent writers.
- 10 iterations per case. Enough to catch a 20% regression, not enough to resolve a 10%
  difference — several comparisons above are explicitly left unresolved for this reason.
- Short cases move between runs; see [Run-to-run bimodality](#run-to-run-bimodality) for which
  rows this affects and why the gate cannot currently see through it.
- Postgres in a Docker VM on macOS. Absolute latencies are not production latencies.
- One machine profile. Results are keyed by machine, and the comparer refuses to diff across
  profiles rather than warning, so these numbers say nothing about CI's hardware.
- `alberto_read_by_types` has a known unfixed plan defect (~30× available); the three wildcard
  read functions have never been audited for the same pattern.
- No comparison against other event stores yet.
