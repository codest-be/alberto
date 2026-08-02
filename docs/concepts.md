# Concepts

Everything Alberto does rests on five ideas: the **log**, **tags**, **queries**, **boundaries**,
and **checkpoints**. This page defines each one and shows the failure mode you get when you
misunderstand it.

If you have not run the sample yet, [getting-started.md](getting-started.md) is a better first
read.

## The log

There is one append-only log per module: `alberto_events`, ordered by a monotonic
`global_position`. That is the only ordering in the system. There are no per-entity streams to
choose between, no partitions to route to, nothing to shard by at write time.

Every event carries:

| | |
|---|---|
| `global_position` | Its place in the total order. Assigned on append. |
| `event_type` | The `[EventType("...")]` slug. |
| `tags` | The concepts it touches, extracted from `[Tag]` properties, plus the framework-reserved `_version:N` schema-version tag. See [events.md](events.md#reserved-_-tag-concepts). |
| `event_data` | The JSON payload. |
| `metadata` | Free-form headers — trace context lives here. |

Two side tables, `alberto_event_type_positions` and `alberto_event_tag_positions`, index the log by
type and by tag. They are what makes a boundary query cheap enough to run on every write.

## Events

```csharp
[EventType("order-placed")]
public sealed record OrderPlaced(
    [property: Tag("order")]    Guid OrderId,
    [property: Tag("customer")] Guid CustomerId,
    decimal Amount) : IEvent;
```

**`[EventType("…")]` is the on-disk name.** Lowercase letters, digits, hyphens and underscores
only; anything else throws at construction. Rename the C# record whenever you like — this string
is what old rows say and must never change.

**`[Tag("concept")]` marks a property as a concept the event concerns.** Concept and id both accept
`[a-zA-Z0-9_-]+`. The stored tag is `concept:id`.

Not every property is a tag. `Amount` above is data; `OrderId` and `CustomerId` are handles other
decisions will reach for. The rule of thumb: **tag anything a future consistency boundary might be
drawn around.** Adding a tag later means backfilling, so err on the side of one more.

`WithEventsFrom(assembly)` scans an assembly for `[EventType]` records and builds the serializer
that maps between them and the log. Reading an event whose type is not in that assembly throws
`InvalidOperationException` naming the missing slug. `WithEventsFrom` takes exactly one assembly,
so keep a module's events together; if you genuinely need several, build the serializer yourself
with `EventSerializer.FromAssemblies(…)` and register the `AlbertoStore` by hand.

### Two traps, both silent

**Guid formatting.** Tag values are extracted with `ToString("D")` — hyphenated. A query built with
`:N` formatting matches *nothing*, and matching nothing is not an error: your fold returns initial
state, your guard sees a clean slate, and you happily double-book a seat.

```csharp
DcbQuery.ByAllTags($"show:{showId:N}", …)                       // ✗ silently matches nothing
DcbQuery.ByAllTags(new EventTag("show", showId.ToString()), …)  // ✓
```

**`[property:]` on records.** `[Tag("order")]` on a primary-constructor parameter binds to the
*parameter*, not the property, and no tag is written. It must be `[property: Tag("order")]`. The
symptom is identical to the one above.

## Queries

A `DcbQuery` selects events along two axes — type and tag — and says how the axes combine.

```csharp
DcbQuery.ByTypes("order-placed", "order-cancelled");   // either type, any tag
DcbQuery.ByTags(new EventTag("order", id));            // ANY of these tags
DcbQuery.ByAllTags(tagA, tagB);                        // ALL of these tags
DcbQuery.For("order", orderId);                        // shorthand for one exact tag
```

Composition rules, in full:

- **Multiple types always OR.** An event matches if it has any listed type.
- **Multiple tags OR by default.** `ByAllTags` switches the tag axis to AND.
- **Types and tags AND across axes** (`CompositionMode.Intersect`, the default). `.AsUnion()` opts
  into OR across axes, `.AsIntersect()` goes back.

```csharp
// Events of this type, for this order.  ← the usual thing you want
DcbQuery.For("order", orderId).WithType<OrderPlaced>();

// Events of this type, OR anything about this order.  ← deliberately wider
DcbQuery.For("order", orderId).WithType<OrderPlaced>().AsUnion();
```

Tags match exactly, on the whole `concept:id` pair. There is deliberately **no** way to query a
concept as a whole — "every event tagged with any order" is not a query this store supports. A
boundary that wide serialises every order against every other, and the only way to answer it
quickly was an index on every tag row ever written, paid for on every append by everyone. A query
is always scoped to the entities it names. `order:*` is not a wildcard; it is a tag with an id of
`*`, which is not a legal id, so the DSL rejects it.

### `ByTags` vs `ByAllTags` is a contention decision

`ByTags("show:S", "seat:A12")` matches every event about the show *or* about any A12 anywhere — a
boundary that covers the whole show, so every seat in it serialises against every other. Use
`ByAllTags`. Getting this backwards does not corrupt anything; it just quietly destroys your
concurrency.

## Consistency boundaries

A boundary is a query used as an **append condition**. The pattern is always three steps:

1. Fold the boundary events into state and record the position read.
2. Decide which events to emit.
3. Append under the same boundary, supplying the position from step 1 as the expected position.

The append succeeds only if nothing matching `boundary` was written after that position. If
something was, the store throws `DcbConflictException` carrying `ExpectedPosition`,
`ConflictingPosition` and the `Query` — retry the whole read-decide-append, since your state is now
stale by definition.

The command pipeline in `Alberto.Commands` does all of this for you:

```csharp
await store.Handle(command)
    .Validate(cmd => …)                              // optional; returns Result
    .Load(boundary, initialState, applyFn)           // folds AND captures the position
    .Decide((cmd, state) => …)                       // → events, or a Problem
    .RetryOnConflict(3)                              // optional; re-reads and re-decides
    .Commit(ct);                                     // appends under the boundary
```

`Commit(ct)` takes no boundary because `Load` already established one. That is enforced by the
type system rather than at runtime: `Load` returns a *bound* pipeline and only a bound pipeline has
`Commit(ct)`.

When state comes from somewhere other than the log — a cache, a read model — use `LoadUnbound`, or
skip loading entirely and `Decide` straight off `Handle`. Neither establishes a boundary, so the
pipeline that comes back offers only `Commit(query, expectedPosition, ct)` and
`CommitUnconditionally(ct)`: you have to say what the append is checked against, or say out loud
that it is checked against nothing.

For the rarer case where the boundary is only discoverable *during* the read — you fold one query
to find an id, then fold a second keyed by it — `LoadUnder` takes a loader that returns its state,
its boundary and the position it read at, and gives you back a bound pipeline. It is the escape
hatch, not the default: when the boundary follows from the command, `Load(cmd => boundary, …)` with
the async part in `Enrich` says the same thing and keeps the I/O out of the window.

`RetryOnConflict(n)` bounds the total number of attempts, re-running `Load` and `Decide` against
whatever is in the log now. Anything before `Load` — `Validate`, `Enrich` — runs once and is
reused, so an expensive lookup or an external call is not repeated per attempt. `TryCommit` is the
non-throwing terminal: it returns a failed `Result` carrying a `dcb.conflict` problem instead of
raising `DcbConflictException`.

Retries are bounded, so `Commit` still throws when the boundary stays contended for all `n`
attempts. Reach for `TryCommit` when the caller *branches* on that — falls back, queues, reports
something other than a failure. When it only needs to report, catch `DcbConflictException` and call
`ToProblem()`: it renders exactly what `TryCommit` would have returned, under the same
`DcbConflictException.ProblemCode`, so an error surface handles one shape however the conflict
arrived. That is what the examples' `OrThrow` does.

**This is the whole optimistic-concurrency story.** You never store a version number, and there is
no aggregate whose identity has to be decided up front. The unit of contention is exactly the
question you asked.

### Choosing a boundary

Make it as narrow as the rule requires and no narrower:

| Rule | Boundary |
|---|---|
| "This seat can be taken once" | `ByAllTags(show:S, seat:A12)` |
| "A customer may hold at most 4 seats" | `For("customer", id)` |
| "A show cannot oversell" | `For("show", id)` |

The third boundary serialises the whole show — correct, and deliberately expensive. The first does
not contend with anything else at all. Both are queries over the same events; nothing had to be
written twice to support both.

## Positions and checkpoints

`global_position` is the log's clock. Everything that consumes the log tracks how far it has read
as a position, called a **checkpoint**, stored per processor in
`alberto_processor_checkpoints`.

- Checkpoint writes are **monotonic** — the upsert uses `GREATEST`, so a processor cannot move
  itself backwards, even after a restart that resurrects a stale in-memory value.
- `RewindAsync` is the single deliberate escape hatch that writes unconditionally. It exists for
  operators, and it is what `alberto ops checkpoint set` calls.
- `SaveIfLeaseHeldAsync` on `IFencedCheckpointStore` makes the write conditional on still holding
  the processor or tenant lease, so a partitioned replica cannot clobber a newer checkpoint.

**Lag** is `head position − checkpoint position`. It is the number you watch in production:
`alberto status` prints the head and each processor's position, and
[operations.md](operations.md#reading-lag) has the one-liner that subtracts them.

## Processors

A **processor** is anything with a checkpoint that consumes the log. Three kinds ship:

| Kind | Does | Page |
|---|---|---|
| Projection | Writes read-model state | [projections.md](projections.md) |
| Reactor | Performs side effects | [reactors-and-outbox.md](reactors-and-outbox.md) |
| Outbox handler | Turns events into external messages | [reactors-and-outbox.md](reactors-and-outbox.md) |

Each has its own checkpoint and can be rewound, retried or rebuilt independently. One **control
loop** per module drives them all: read a batch after the checkpoint, dispatch it through the
middleware chain, save the checkpoint, sleep. Details in
[architecture/async-processing.md](architecture/async-processing.md).

Projections and reactors can also run **inline** — synchronously inside `AppendAsync`, before it
returns — trading read-your-writes consistency for latency and coupled failures.

## Modules

`AddAlberto("orders", builder => …)` declares one module: one log, one control loop, one set of
processors, one Postgres schema. Everything inside is registered in DI **keyed by the module
string**, so one application can host several:

```csharp
var backend = sp.GetRequiredKeyedService<IEventStoreBackend>("orders");
var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>("orders");
```

Forget the key and you get a `InvalidOperationException` at resolve time, not the wrong module's
store.

## The two store interfaces

| Interface | For | Gives you |
|---|---|---|
| `IEventStore` | Application code | Appends with boundary conditions, tenancy applied |
| `IEventStoreBackend` | Infrastructure | The raw log — `StreamAsync`, `StreamAllAsync`, `GetLastPositionAsync` |

```csharp
Task<...> AppendAsync(IEnumerable<IEventToPersist> events, DcbQuery? condition = null,
                      long? expectedPosition = null, CancellationToken ct = default);
Task<...> StreamAsync(DcbQuery query, long afterPosition = 0, int? limit = null,
                      CancellationToken ct = default);
```

Read models and queries should go through a projection, not `StreamAsync` — folding a large
boundary on every request is exactly the cost projections exist to avoid. `StreamAsync` on the
request path is fine for a *narrow* boundary (one order, one seat) where you want the log's
consistency rather than a projection's eventual one.

## Where things live in Postgres

One schema per module, containing:

| Table | Holds |
|---|---|
| `alberto_events` | The log |
| `alberto_event_type_positions`, `alberto_event_tag_positions` | Query indexes |
| `alberto_processor_checkpoints` | Per-processor positions, and the fence token that guards them |
| `alberto_processor_leases` | Which replica currently owns each processor, and until when |
| `alberto_projection_states` | Projection documents, keyed by rebuild version |
| `alberto_projection_rebuild_meta` | Rebuild state machine |
| `alberto_dead_letter_events` | Events that exhausted their retries |
| `alberto_outbox_entries` | Outbox, `pending → processing → delivered/failed` |
| `alberto_tenants`, `alberto_tenant_leases`, `alberto_tenant_assignments` | Multi-tenancy |

One further table, `alberto_tenant_shards`, exists only if you shard a module's tenants across
databases — and it lives in a separate control database rather than in any module's schema. See
[tenant sharding](architecture/tenant-sharding.md).

Migrations are DbUp scripts embedded in `Alberto.Postgres` and run automatically when
`PostgresOptions.AutoMigrate` is true (the default). Set it false and run them from your own
migration step if you would rather control when DDL happens.
