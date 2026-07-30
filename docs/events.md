# Event Types and Schema Versioning

This page covers how Alberto identifies events on disk, how it handles schema evolution,
and the upcaster API for migrating persisted payloads to a newer CLR type.

If you have not read [concepts.md](concepts.md) yet, start there — this page assumes you are
familiar with the `[EventType]`, `[Tag]`, and `DcbQuery` fundamentals.

---

## Event type slugs are permanent

Every event record declares an `[EventType("...")]` slug:

```csharp
[EventType("order-placed")]
public sealed record OrderPlaced(
    [property: Tag("order")]    Guid OrderId,
    [property: Tag("customer")] Guid CustomerId,
    decimal Amount) : IEvent;
```

The slug is the on-disk name written into the `event_type` column of every stored row.
Two side tables — `alberto_event_type_positions` and `alberto_event_tag_positions` — index
the log by type and by tag respectively; both are keyed by the slug string. Boundary queries
such as `DcbQuery.ByTypes("order-placed")` filter against those indexes.

**The slug must never change after events have been written.** Renaming it is equivalent to
deleting the old event type: every row still in the database uses the old string, so a query
for the new name matches nothing, and a boundary that should cover those rows silently misses
them. Rename the C# record freely; leave the slug alone.

```csharp
// Before — a rename of the record that is safe:
[EventType("order-placed")]
public sealed record OrderCreated(...) : IEvent;   // old name: OrderPlaced

// The slug "order-placed" is still what every stored row says.
// ByTypes("order-placed") still matches all of them.
```

---

## Where the version lives

When a record's payload shape changes, Alberto tracks the **schema version** in two places:

| Location | What it holds | Used for |
|---|---|---|
| `[EventType("...", Version = N)]` attribute | The version this CLR type represents | Stamping the `_version:N` tag; picking which upcaster step to call |
| `_version:N` tag on every stored event | The version the event was written as | Determining whether to upcast on read |
| `EventType.Version` in memory | The version of the deserialized envelope | Upcaster selection |

The version is **not** part of the event type slug and is **not** part of `EventType` equality.
`EventType.Equals`, `GetHashCode`, and the implicit `string` conversion all use the slug only.
This matters for boundaries: `DcbQuery.ByTypes("order-placed")` matches version 1 events and
version 2 events alike without any change at the boundary declaration site.

```csharp
// Both of these are the same event as far as DcbQuery is concerned:
[EventType("order-placed")]               // Version defaults to 1
public sealed record OrderPlacedV1(Guid OrderId, decimal Amount) : IEvent;

[EventType("order-placed", Version = 2)]  // Explicit Version = 2
public sealed record OrderPlaced(Guid OrderId, string Currency, decimal Amount) : IEvent;
```

Events written before schema versioning was introduced carry no `_version` tag. Alberto reads them as
version 1. That is the correct assumption for records that were in production before the versioning
feature existed.

---

## Declaring a new version

Add `Version = N` to the attribute, where N is one greater than the highest version currently in
the store. The old record stays in the codebase as a transitional type (or can be an anonymous
container) that the upcaster uses to read old payloads:

```csharp
// Old shape — used only by the upcaster; no longer the live record
internal sealed record OrderPlacedV1(Guid OrderId, decimal Amount) : IEvent;

// New shape — what new events and projections use
[EventType("order-placed", Version = 2)]
public sealed record OrderPlaced(
    Guid OrderId, string Currency, decimal Amount) : IEvent;
```

Alberto validates that the version number is at least 1. Version 0 fails with `ArgumentException`
at construction time.

---

## Reserved `_` tag concepts

**The entire `_` prefix is reserved for framework tags**, not just the names Alberto uses today.
Domain concepts are things like `order`, `customer`, or `venue`, so nothing useful is lost — and
it means any framework tag added in a later version lands in space that was never available to
your code, instead of colliding with whoever had already picked that name.

The one reserved concept in use is `_version`, written on every event by
`EventSerializer.ExtractTags`. Application code cannot author a leading-underscore tag:

- The `EventTag` public constructor throws `ArgumentException` when the concept starts with `_`.
- `[Tag("_anything")]` on an event property throws `ArgumentException` when the first event of
  that type is appended — the tag attributes are scanned lazily on first use, not at startup.
- Boundaries need no separate guard: `DcbQuery` takes `EventTag` values, so a query over a
  reserved concept cannot be constructed at all.

The constant is `EventTag.ReservedConceptPrefix`, and `EventTag.SchemaVersionConcept` is the
concept name itself if you need to compare against it when iterating tags.

The tag appears in `IEventEnvelope.Tags` and in the raw database array alongside application
tags. Projection handlers and evolvers that iterate the tag collection will encounter it.
Skip it: you cannot construct a matching query over it through the public API, so filtering
on it would be a no-op anyway.

---

## The upcaster API

An upcaster is a chain of per-version transformation functions that turns an old JSON payload
into the current CLR type. Build one with `DeclareUpcaster` and register it with an
`UpcasterRegistry`:

```csharp
// 1. Declare the chain — every version step from the oldest to the current
var decl = DeclareUpcaster.For<OrderPlaced>("order-placed")
    .From<OrderPlacedV1>(                      // reads JSON as OrderPlacedV1 (version 1)
        fromVersion: 1,
        transform: v1 => new OrderPlaced(
            v1.OrderId,
            Currency: "USD",                   // added in v2 with a default
            v1.Amount))
    .Build();

// 2. Build the registry — one declaration per event type
var registry = UpcasterRegistry.Create()
    .Add(decl)
    .Build();

// 3. Register it on the serializer
var serializer = EventSerializer
    .FromAssemblies(typeof(OrderPlaced).Assembly)
    .WithUpcasters(registry);
```

### Chaining across multiple versions

Each `.From<T>(fromVersion, transform)` step covers one version transition. Add one step
per version, with no gaps and no duplicate source versions:

```csharp
var decl = DeclareUpcaster.For<OrderPlacedV3>("order-placed")
    .From<OrderPlacedV1>(fromVersion: 1, v1 => new OrderPlacedV2(v1.OrderId, Amount: v1.Amount))
    .From<OrderPlacedV2>(fromVersion: 2, v2 => new OrderPlacedV3(v2.OrderId, v2.Amount, Tax: 0m))
    .Build();
```

`DeclareUpcaster.Build()` throws immediately if there is a gap (say, version 1 → 3 with
nothing covering 2) or a duplicate source version.

`UpcasterRegistry.Create().Add(...).Build()` throws if two declarations cover the same event
type id.

### When upcasting applies

`EventSerializer.Deserialize` applies the upcaster chain when the event's stored version is
less than the current version declared on the CLR type. Events already at the current version
are returned directly.

**The control loop applies upcasting.** When the control loop reads a batch of events and
dispatches them to projections and reactors, it goes through `EventSerializer.Deserialize`.
Projection handlers and reactor handlers receive the current CLR type; they do not need to
know about older versions.

**`AlbertoStore.Fold<TState>` applies upcasting.** This method is the direct fold API on
`AlbertoStore`; it calls `EventSerializer.Deserialize` and therefore runs the upcaster chain.

**The command pipeline's `Load<TState>` method applies upcasting.** When
`CommandPipeline.Load<TState>(boundary, evolver)` is called, it threads `EventSerializer.Deserialize`
into the `Evolver<TState>` dispatch loop. Every envelope is passed through `EventSerializer` — and
therefore through the upcaster chain — before the evolver handler sees it. The handler always
receives the current CLR type.

**Calling `Evolver.Reconstitute(envelopes)` directly (without a serializer) does not apply
upcasting** and will throw `InvalidOperationException` if any envelope was stored at an older
schema version than the handler type expects. A silent wrong answer is worse than a loud
failure. If you need to call the evolver directly, use the serializer-threaded overload:

```csharp
// Correct when upcasting may be needed:
var state = evolver.Reconstitute(envelopes, initial: default, serializer.Deserialize);

// Safe only when all events are at the current schema version:
var state = evolver.Reconstitute(envelopes);
```

The same applies to `DeciderExtensions.DecideAndAppendAsync`: the five-argument overload
(without a serializer) will throw for stale-version envelopes; prefer the six-argument overload
that accepts an `EventSerializer`.

### Limits of upcasting

An upcaster transforms an existing payload; it cannot invent data that was never captured.

If a v1 event is missing a field that the v2 shape requires — and there is no sensible default
for it — an upcaster cannot recover the missing value. The only options are:

- **Choose a real default.** The worked example below does this: old `order-placed` events
  had no `Currency` field, so they are upcasted to `Currency: "USD"` — a deliberate, chosen
  default, not recovered data. Record this assumption in comments and in your projection logic,
  because projections that depend on that default inherit the assumption silently.
- **Backfill the rows.** Write a one-off migration that reads the original context (from a
  related event or a side table) and rewrites the old payloads with the real value. Upcasters
  then only have to transform the backfilled rows, and the gap is closed permanently.

There is no third option. An upcaster receives only the JSON that was stored at write time.

---

## Worked example

**Scenario:** the `order-placed` event gains a `Currency` field. Existing events have no
currency. New decisions must produce a currency-aware result; projections must backfill USD
for old events.

### Step 1 — Add the attribute and a transitional type

```csharp
// Events that carry the old shape
internal sealed record OrderPlacedV1(Guid OrderId, Guid CustomerId, decimal Amount) : IEvent;

// The live record — Version = 2 from this point forward
[EventType("order-placed", Version = 2)]
public sealed record OrderPlaced(
    [property: Tag("order")]    Guid OrderId,
    [property: Tag("customer")] Guid CustomerId,
    string Currency,
    decimal Amount) : IEvent;
```

### Step 2 — Declare the upcaster

```csharp
var decl = DeclareUpcaster.For<OrderPlaced>("order-placed")
    .From<OrderPlacedV1>(fromVersion: 1, v1 => new OrderPlaced(
        v1.OrderId, v1.CustomerId,
        Currency: "USD",    // default for old events
        v1.Amount))
    .Build();
```

### Step 3 — Register it on the module builder

Pass the declaration to `AddUpcaster` on the module builder. `WithEventsFrom` picks up every
declaration added via `AddUpcaster` and wires the upcaster registry into the `EventSerializer`
it builds internally:

```csharp
services.AddAlberto("orders", builder => builder
    .WithPostgres(o => o with { ConnectionString = cs })
    .WithEventsFrom(typeof(OrderPlaced).Assembly)
    .AddUpcaster(decl));
```

`AddUpcaster` can be chained once per event type that has schema versions:

```csharp
services.AddAlberto("orders", builder => builder
    .WithPostgres(o => o with { ConnectionString = cs })
    .WithEventsFrom(typeof(OrderPlaced).Assembly)
    .AddUpcaster(orderPlacedDecl)
    .AddUpcaster(orderCancelledDecl));
```

### What happens at runtime

Events written after step 2 are stored with `_version:2` in their tag array and deserialize directly
to `OrderPlaced`. Events written before step 2 have no `_version` tag and are read as version 1.
When the control loop feeds them to a projection, `EventSerializer.Deserialize` detects
`version (1) < currentVersion (2)`, calls the upcaster, and the projection handler receives
an `OrderPlaced` with `Currency = "USD"`.

No migration of existing rows is needed; the upcaster runs on every read of an old event.
