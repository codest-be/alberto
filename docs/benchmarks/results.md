# Benchmark results

Every number here comes from `benchmarks/results/local-3575380c/baseline.json`, measured on
one machine in one sitting. How to run and compare: [benchmarks/README.md](../../benchmarks/README.md).

Migrations are referenced below by their original number. The 34 numbered scripts were later
consolidated into two per tenancy set, so those links all point at
`SingleTenant/002_QueryFunctions.sql`, which keeps each one as a titled section
(`-- Alberto DCB Event Store - Migration 031`) along with its original rationale.

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

| Case | Mean | ±sd | Allocated | Previous baseline |
|---|---:|---:|---:|---:|
| `SingleAppend` | 919 µs | 10.2% | 11.5 KB | 987 µs |
| `AppendWithDcbCheck` | 947 µs | 8.0% | 12.0 KB | 1444 µs |
| `AppendWithConflictDetected` | 674 µs | 10.2% | 21.3 KB | 922 µs |

**Nothing in this change touches the append path, and the whole family still moved — that is
the point of the last column.** The previous baseline caught these three cases in their high
mode; this run caught them in their low mode, and the earlier revision's isolated re-run
(948 / 974 / 715 µs) landed in the low mode too. Three measurements of identical code, two
modes, and the run rather than the code decides which (see
[Run-to-run bimodality](#run-to-run-bimodality)). The gate compares against whichever run was
promoted, so these three cases will read as "improved" against the old baseline and would read
as "regressed" the other way round. **Neither is a code change.**

**The DCB consistency check is not a meaningful tax.** 919 µs against 947 µs is +3.1%, well
inside both cases' standard deviations, and the earlier re-run put the same pair at +2.8%.
Appending with a consistency boundary costs about the same as appending without one. That is
the important result: DCB's whole premise is that the boundary is checked in the same round
trip as the write, and the measurement is consistent with that. The old baseline's high mode
made the same comparison read as +46%, which is exactly the trap the bimodality section exists
to flag.

The conflict path is *cheaper* than the success path in both runs, which is the right shape —
a detected conflict aborts before doing the insert work. Its higher allocation (21.3 KB vs
12.0 KB) is the exception object and its message.

### Batching

| Batch size | Mean | Per event | Allocated | Per event |
|---:|---:|---:|---:|---:|
| 1 (`SingleAppend`) | 919 µs | 919 µs | 11.5 KB | 11.5 KB |
| 10 | 1615 µs | 162 µs | 29.1 KB | 2.9 KB |
| 100 | 4570 µs | 45.7 µs | 197.9 KB | 2.0 KB |
| 1000 | 29639 µs | 29.6 µs | 1924.4 KB | 1.9 KB |

**This is the single biggest operational lever in the suite.** Batching 1000 events costs
30 µs each against ~920–990 µs each one at a time — call it a 30× efficiency gain — and the
per-event allocation flattens at about 1.9 KB. The curve is steeply concave: batches of 10
already recover 6× of that, and 100 recovers 20×. Most of the win is available well before you
need 1000-event batches, which matters because a large batch is one transaction holding locks
for 30 ms.

Read the other direction, this quantifies the cost of *not* batching. A reactor that appends
per event is paying a round trip and an fsync per event, and nothing in Alberto can recover
that for it.

### Tag fan-out

| Tags per event | Mean | Allocated |
|---:|---:|---:|
| 1 | 882 µs | 11.5 KB |
| 5 | 970 µs | 16.8 KB |
| 20 | 1120 µs | 20.0 KB |

**Tag fan-out is cheap, but this is the one row in the suite whose number should not be quoted
to three digits.** The 20-tag case moves between runs: six measurements of the same code have
now given 1086, 1120, 1152, 1226, 1569 and 1602 µs. A four-run view of this case once looked
like two tight clusters; the two values since sit between them, so that reading was too clean
and is withdrawn — what can be said is that the spread is wide and reproducible, not that it is
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
| `GetLastPosition` | 340 µs | 233 µs | 266 µs | flat |
| `GetStableHead` | 468 µs | 296 µs | 362 µs | flat |
| `BoundaryRead` | 706 µs | 516 µs | 422 µs | flat |
| `StreamAllFromZero` | 655 µs | 681 µs | 847 µs | flat |
| `TailRead` | 695 µs | 809 µs | 682 µs | flat |
| `StreamByTag` | 688 µs | 1494 µs | 1438 µs | grows, then flat |
| `StreamByTypeAndTag` (1 type) | 532 µs | 918 µs | 4632 µs | grows |
| `StreamByTypesAndTag` (3 types) | 769 µs | 1507 µs | 5010 µs | grows |
| `StreamByType` | 1068 µs | 1764 µs | 1229 µs | flat |
| `StreamByMultiTag` (2 tags) | 749 µs | 1427 µs | 1457 µs | grows, then flat |
| `StreamByMultiTag` (8 tags) | 1374 µs | 2438 µs | 1907 µs | grows, then flat |

**The headline is that the unfiltered reads are flat across a 100× growth in the log.** That
is the property an event store lives or dies by: a paged read should cost what it *returns*,
not what the store *holds*. `StreamAllFromZero` and `TailRead` sit within 10% of themselves at
every size, and position lookups allocate nothing at all and answer in a few hundred
microseconds throughout.

**Where a filtered read looks cheap, check whether it returned less** — and with twenty types
the allocation column now predicts exactly how much less. A full page costs ~330 KB, about
660 bytes per event, so allocations divide back into a row count. The seed holds 100 order
tags, so a single tag matches `storeSize/100` events, and naming *k* of twenty types keeps
*k*/20 of those:

| Case | 10k | 100k | 1M |
|---|---|---|---|
| `StreamByTypeAndTag` predicted | 5 | 50 | 500 (page-capped) |
| …allocated | 12.3 KB | 33.5 KB | 291 KB |
| `StreamByTypesAndTag` predicted | 15 | 150 | 1500 → 500 |
| …allocated | 15.0 KB | 96.3 KB | 336 KB |

96.3 KB is 146 events against a predicted 150, and 33.5 KB is 51 against 50. The arithmetic and
the measurement agree, which is the strongest evidence in this document that the seed does what
it claims. It also means **the two-axis cases are the only reads that reach a full page at 1M
and not before**, so their 10k and 100k columns are cheap for the boring reason.

**`StreamByType`, `StreamByTag` at 100k+ and `StreamByMultiTag` return a full page at every
size**, so their curves are like-for-like. `StreamByTag` goes 1494 → 1438 µs (−4%) over the
last 10×, and `StreamByMultiTag` 1427 → 1457 µs (+2%) at two tags and 2438 → 1907 µs (−22%) at
eight. Those are the flat-at-scale shapes you want.

**`StreamByType` was the slowest read in the suite and is now among the flattest.** It reads
1068 / 1764 / **1229** µs, against 1494 / 1931 / **9332** µs before
[migration 031](../../src/Alberto.Postgres/Migrations/SingleTenant/002_QueryFunctions.sql)
— **−86.8% at 1M**, the largest single move the suite has recorded. The 1M column now costs
about what the 10k column costs, which is the property this whole document is about: a paged
read should cost what it returns, not what the store holds. What the fix had to be, and why it
is not the one migration 030 used, is [below](#migration-031-the-one-axis-read).

**`StreamByTypeAndTag` was the one read that genuinely scaled with the store, and the fix
sequence is worth keeping because each step exposed the next.** All three of these figures come
from the old three-type corpus and are a self-consistent series:

| | 10k | 100k | 1M |
|---|---:|---:|---:|
| `INTERSECT` (original) | 1213 µs | 4052 µs | 34776 µs |
| [028](../../src/Alberto.Postgres/Migrations/SingleTenant/002_QueryFunctions.sql) semi-join | — | 2672 µs | 6042 µs |
| [029](../../src/Alberto.Postgres/Migrations/SingleTenant/002_QueryFunctions.sql) scalar fast path | 588 µs | 2676 µs | 3205 µs |

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

On today's twenty-type corpus the same case reads **532 / 918 / 4632 µs**. The 1M column is
higher than 029's 3205 µs and that is not a regression — a tag∩type cell now holds ~500 events
instead of ~3300, so filling a 500-event page means traversing essentially the whole 10,000-row
tag range rather than the first sixth of it. Same code, harder question. The 100k column fell
by two thirds (2676 → 918 µs) for the mirror-image reason: it now returns ~50 events instead of
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

[Migration 030](../../src/Alberto.Postgres/Migrations/SingleTenant/002_QueryFunctions.sql)
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

Through the suite, migration 030 moved `StreamByTypesAndTag` from 675 / 2783 / 14731 µs to
**540 / 1386 / 4681 µs** — −20% / −50% / **−68%** — and left the single-type sibling untouched.
At 1M the two then cost the same (4681 vs 4829 µs) for the same 500-event page, which is the
result worth having: a boundary that names three of a context's event types is no longer
penalised for not naming exactly one.

Neither branch needs a `DISTINCT`. An event carries exactly one `event_type`, so testing it —
on the type-position primary key or on the events row — cannot make a position match twice, and
a single scalar tag yields at most one tag-position row per position. No result can consume two
slots of `p_limit`.

Those five figures are the 030-era run, kept as the before/after that justified the migration.
The current baseline reads the same case at **769 / 1507 / 5010 µs**, against its single-type
sibling's 532 / 918 / 4632 — the same shape, measured a run or two later and correspondingly
noisier (see [Run-to-run bimodality](#run-to-run-bimodality)). Migration 031 does not touch
this function: it has a tag axis, and 031 changed only the one-axis read.

### Migration 031: the one-axis read

The defect was the same `= ANY` planner opacity that migrations 028–030 removed from the
two-axis read, still present on the one-axis read. `EXPLAIN` on the shipped
`alberto_read_by_types` showed its generic plan never using the
`(tenant_id, event_type, global_position)` index at all: it sequentially scanned the whole of
`alberto_event_type_positions` — one row per event in the store — filtered, sorted, and
merge-joined the result against `alberto_events` walked in position order. At 1M it sorted
about 150,000 positions to return 500.

The corpus is what made it visible. At 1M, `order-placed` is 50,133 of the million events and
the 500th one sits at global position 9,557; with the old three-type corpus the 500th would
have sat near 1,500, so the same plan walked ~6.4× fewer `alberto_events` rows and the case
measured 2436 µs. Same code, harder question — and then a real defect underneath it.

**031 does not copy 030's remedy, and measuring is what settled that.** 030's second branch
drops the type-position index and tests `event_type` on the `alberto_events` row the query has
to fetch anyway; that works there because the *tag* scan bounds how many rows are ever
considered. This function has no tag axis, so nothing bounds the scan, and the shape degrades
from cheap to reading the whole log exactly when the query is most selective. Candidate
timings as plpgsql functions under `SET plan_cache_mode = force_generic_plan`, min of 25 warm
calls, twenty-type corpus, limit 500 from position 0 (ms) — *k* is how many of the twenty types
the query names, and "absent" names a type no event carries:

| Single-tenant, 1M | k=1 | k=3 | k=10 | k=20 | absent |
|---|---:|---:|---:|---:|---:|
| shipped body | 23.561 | 28.233 | 44.631 | 60.021 | 17.221 |
| type tested on the events row (030's trick) | 0.764 | 0.306 | 0.186 | **0.149** | 66.183 |
| bounded probe per type (031) | 0.319 | 0.410 | 0.643 | 0.926 | **0.026** |
| 029's scalar probe | 0.312 | n/a | n/a | n/a | n/a |

| Multi-tenant, 2 × 1M | t1 k=1 | t1 k=3 | t1 k=10 | t2 k=3 | absent |
|---|---:|---:|---:|---:|---:|
| shipped body | 50.191 | 8.242 | 29.816 | 70.958 | 2.184 |
| type tested on the events row | 0.783 | 0.359 | 0.208 | 53.234 | 183.814 |
| bounded probe per type (031) | 0.329 | 0.461 | 0.733 | **0.454** | **0.032** |

The two right-hand columns are the argument. Tenant t1 holds the first half of the log and t2
the second, so t2 is what any tenant that did not start writing first looks like. 030's trick
wins the middle of the table and loses catastrophically at both edges, and both edges are the
same thing: an ordered walk of `alberto_events` from `p_after_position` that travels a long way
before the `LIMIT` is satisfied. Probing per type is within noise of the best shape at one type,
costs about 30 µs per additional named type, and has no edge — naming all twenty types still
beats the shipped body by 65×, and naming a type that does not exist costs 26 µs instead of
66 ms. So 031 ships one uniform body with no guard, where 029 and 030 both needed one.

The probe source is deduplicated, and that is load-bearing rather than tidy. `DcbQuery`
concatenates types without deduplicating, so `ByTypes("a").WithTypes("a")` reaches the function
as `{a,a}`; one probe per array element would then return every position twice. Measured
without the `DISTINCT`, a 500-row page held 327 distinct positions — no error, just a third of
the caller's page spent on duplicates. The `= ANY` form was immune to this by accident, so the
`DISTINCT` preserves the old behaviour rather than optimising anything. Given a deduplicated
source, no `DISTINCT` over *positions* is needed: an event carries exactly one `event_type`, so
one type's probe cannot repeat a position and two distinct types' probes cannot collide.

Two notes on what did **not** change. `StreamByType[100k]` reads 1764 µs with a 93% standard
deviation — it is bimodal in this run, not slower than its 1M sibling; the 10k and 1M columns
(6.4% and 5.6% sd) are the trustworthy ones, and a confirmation run put 100k at 1171 µs. And
the three wildcard readers filed as never-audited follow-up work
(`alberto_read_by_tag_patterns`, `_types_or_tag_patterns`, `_types_and_tag_patterns`) have no
live body to audit: [migration 024](../../src/Alberto.Postgres/Migrations/SingleTenant/002_QueryFunctions.sql)
dropped all three, and `MigrationUpgradeAndParityTests` asserts they stay dropped.
`alberto_read_by_tags`, `_types_or_tags` and `_by_all_tags` still carry `= ANY` on the tag axis
and are a separate question, because a tag axis genuinely can duplicate positions. That question
is [answered below](#migration-033-the-tag-axis), and it turned out to have three answers.

**Tag unions are cheaper than they look.** Eight tags cost roughly 1.5× two tags, not 4×, and
neither grows with the store. The union is served by one index range scan per tag against
`(tag, global_position)`, so adding tags adds scans over already-ordered data rather than
multiplying work.

### Migration 033: the tag axis

The follow-up 031 filed. Five functions still matched tags with `= ANY`; one of them was already
fine and four were not, in three different ways.

`alberto_read_by_tags` needed nothing — migration 009 had already rewritten it as one bounded
probe per tag, which is the same shape 031 later arrived at independently on the type axis. The
other four each put a **blocking node** above the opaque scan: a `GROUP BY … HAVING
COUNT(DISTINCT tag)` in the two all-tags functions, a `UNION` dedup over two unbounded arms in
`_types_or_tags`, and a `SELECT DISTINCT` over whole event rows — two `jsonb` columns included —
in `_types_or_all_tags`. A blocking node is worse than opacity alone: opacity costs a `Sort`,
but a `Sort` also removes the accidental early exit a `LIMIT` might otherwise have got. Measured
plan for `alberto_read_by_all_tags` on one tag carried by half the log:

```
Limit (actual rows=500)
  -> GroupAggregate (actual rows=500)
       Filter: (count(DISTINCT tag) = array_length($1, 1))
       -> Sort  Sort Method: external merge  Disk: 13720kB
            -> Index Only Scan (cost rows=47357) (actual rows=500000)
```

Half a million index rows read and spilled to disk to return five hundred, on an estimate off by
a factor of ten. The diagnostic that separates this from ordinary slowness is dropping the limit
from 500 to 10: a correctly bounded read gets about 15× cheaper, and all four of these stayed
flat.

**The union shapes are 031's remedy applied to both axes at once.** Each named type gets its own
scalar probe with its own `LIMIT`, each named tag likewise; the arms are merged, top-N sorted and
re-limited. Bounding an arm at `p_limit` is safe for the reason 031 gives: if a position belongs
in the true first `p_limit` of the union, at most `p_limit − 1` positions precede it, so it is
within the first `p_limit` of whichever arm produced it. One detail is easy to get wrong and
silent when you do — a trailing `ORDER BY … LIMIT` after a `UNION` binds to the *whole union*, so
each arm has to be parenthesised to be individually bounded. Written the obvious way, the fix
applies to one arm and the function stays slow.

**The all-tags shapes cannot use that remedy, and the reason is instructive.** Under AND
semantics an event matching every named tag must appear in *every* tag's list, so no single
probe's first `p_limit` rows are a complete candidate set. But that same fact supplies a
different fix: every match carries the driving tag, so one scalar probe on *one* tag is complete
on its own. It is an ordered range scan the `LIMIT` terminates as soon as `p_limit` matches are
found, and each candidate tests the remaining tags with one index probe apiece. `GROUP BY`,
external sort and `= ANY` all disappear together.

**Which tag drives has to be decided at runtime, and that is the genuinely new part.** Under a
generic plan — what a pooled connection gets — the planner has no tag values at all, so it cannot
know which of them is rare. Taking `p_tags[1]` and hoping costs 20× on the shape this is most
likely to meet: one aggregate tag AND one category tag, named in whichever order the caller
happened to write them. `alberto_pick_all_tags_driver` measures instead. For each tag it reads at
most `p_limit` positions above `p_after_position`, index-only, and keeps two facts: whether the
tag ran out before the cap, and how far along the log its `p_limit`-th position sits. A tag that
ran out has fewer rows in range than the caller asked for, which caps the whole conjunction, so
it wins outright. Otherwise the winner is the tag whose `p_limit`-th position is *furthest along*
— the sparsest over the stretch the driving scan will actually walk, which matters more than the
tag's total frequency. The probe costs about 50 µs for two tags and is skipped entirely at one.

Measured at the SQL level under `force_generic_plan`, 1M events, limit 500 from position 0. The
corpus puts one order tag on each event, so every tag reaches about 1% of the log; `order:hot` is
a synthetic tag added to half the corpus, standing in for the tag shapes a real store has and
this corpus does not — a status or category tag, a tenant-wide marker, a busy customer.

| | shipped | 033 |
|---|---:|---:|
| `by_all_tags`, 1 tag @1% | 1.067 | 0.509 |
| `by_all_tags`, 1 tag @50% | 55.544 | **0.557** |
| `by_all_tags`, 2 tags (hot named first) | 53.121 | **1.033** |
| `by_all_tags`, 2 tags (rare named first) | 53.121 | **1.035** |
| `types_and_all_tags`, 1 type + 1 tag @50% | 51.424 | **0.948** |
| `types_and_all_tags`, 3 types + 1 tag @50% | 55.942 | **1.847** |
| `types_or_tags`, 3 types + 1 tag | 84.435 | **0.768** |
| `types_or_all_tags`, 3 types + 2 tags | 390.934 | **1.512** |

The two design choices, isolated:

| | ms | | ms |
|---|---:|---|---:|
| driver chosen by probe | 1.033 | driver = `p_tags[1]` | 20.053 |
| 1 type: scalar type probe | 0.948 | 1 type: type on events row | 4.598 |
| 3 types: type on events row | 1.847 | 3 types: `EXISTS` on type index | 7.755 |

The second and third rows are 030's finding reproduced on a new shape, and 033 keeps 030's
branch for the same reason: at one type the scalar probe into the type-position PK is a single
descent, and from two types up, testing `event_type` on the events row the query must fetch
anyway is cheaper than an index probe per candidate. 030's caveat still holds, which is why the
branch is not widened further — that test is only safe while something else bounds how many
events rows are considered, and here it is the driving tag scan.

**One behaviour change, and it is a bug fix.** `DcbQuery` concatenates tags without
deduplicating, so `ByAllTags("a").WithTags("a")` reached these functions as `{a,a}`. The old
bodies compared `COUNT(DISTINCT tag)` — one — against `array_length(p_tags, 1)` — two — and so
returned **nothing** for a query that plainly should match every event tagged `a`. The in-memory
backend intersects per tag and was never affected, so the two backends disagreed; 033 removes
every occurrence of the driving tag before testing the rest, which collapses the duplicate and
aligns them. Every other shape was checked to return byte-identical rows to the body it replaced,
in both directions, before being timed.

**None of this is visible to the benchmark gate**, which is the uncomfortable part.
`EventPlan` puts exactly one tag on each event, so `StreamByMultiTag` measures an OR over
several tags and nothing in the suite exercises an AND over several tags at all — the whole
`_all_tags` family, and the driver selection that makes it fast, are unmeasured by the committed
baselines. That is a corpus gap, not a harness gap, and closing it means changing the seed and
resetting every baseline.

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
- **A candidate must be timed inside an actual plpgsql function, not as a standalone
  `PREPARE`/`EXECUTE` of the same SQL.** Earlier revisions of this document explained that as
  "a set-returning plpgsql function cannot use a parallel plan"; `auto_explain` with
  `log_nested_statements` during the 031 work showed the shipped `alberto_read_by_types` body
  getting a `Gather Merge` *inside* the function, so that explanation is wrong and is withdrawn.
  The practice it justified still stands — parameter form, plan-cache mode and estimation all
  differ between the two framings, so every candidate above was wrapped in a real function
  before being timed. The same class of mismatch is what made an earlier draft of 028 37%
  slower at 100k despite a perfectly good custom plan — see that migration's comment block.

### Allocations

A 500-event page allocates ~330 KB, about 660 bytes per event, consistently across every read
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
[Warmup.cs](../../benchmarks/Alberto.Benchmarks/Harness/Warmup.cs), which records what was
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
wide and reproducible with structure that is not established. And the *whole append family*
moves together: the previous baseline caught all three cases high (`AppendWithDcbCheck` 1444 µs)
and the current one caught all three low (947 µs), with an isolated re-run of the intervening
commit also landing low (974 µs) — a larger swing than any previously catalogued case, on the
family whose headline conclusion is the DCB check's cost. The likely culprits remain per-process
environmental state — container placement, CPU frequency, page-cache warmth — but the suite
measures none of them, so the mechanism is still unidentified.

Migration 031's run added one more instance, in a read this time. `StreamByType[100k]` measured
1764 µs with a **93.4%** standard deviation — the widest in the suite, and the one case where
the modes were visible *within* a run rather than only between them. A confirmation run of the
same commit put it at 1171 µs with 4.5% sd, which is where its 10k and 1M siblings sit. The
committed value is the high one.

Three consequences, in order of how much they should change your reading:

1. **Short cases carry a run-to-run mode gap larger than the 20% regression gate.** For
   `GetLastPosition` (10k), `AppendWithTagFanOut` (20 tags), `TailRead` (1M),
   `StreamByType` (100k) and the whole append family, the gate cannot currently distinguish a
   real regression from a mode flip.
2. **The committed baseline holds the high-mode value wherever the two modes were both seen**,
   which under-detects regressions there but never false-alarms; a low baseline would fail the
   build on roughly every other run. That is a property of which run was promoted, not a choice
   made per row — the append family is the exception, committed low because the promoted run
   caught it low and a baseline must be one coherent run. Re-measured values cannot be spliced
   in to fix individual entries, which is why the Appends table above carries the
   re-measurement in its own column instead.
3. **It does not affect the conclusions above.** Every affected case is short (under ~1.8 ms),
   the affected rows are called out where they appear, and the three structural results — 029's
   47%, 030's 68% and 031's 87% — are on multi-millisecond cases, reproduced across independent
   runs, and corroborated by SQL-level measurement of the specific plans involved.

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
- **No read function has a known `= ANY` plan defect left.** The tag axis was audited and fixed
  by [migration 033](#migration-033-the-tag-axis); the three wildcard readers were dropped by
  migration 024 and have no live body to audit.
- **The corpus puts one tag on each event, so AND-over-several-tags is not benchmarked.**
  Migration 033's largest wins are on shapes no committed baseline covers, and they were
  measured at the SQL level against a hand-seeded corpus instead. Closing this means widening
  `EventPlan` and resetting every baseline, which is why it has not been done in passing.
- No comparison against other event stores yet.
