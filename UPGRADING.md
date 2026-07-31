# Alberto DCB — Upgrade Notes

This file collects **every breaking change** introduced across all release cycles.
The most recent cycle is at the top. Older changes follow.

---

## net10.0 only; public surface hardened; timestamps, record shapes, tag reservation, and metric dimensions

The changes in this cycle close gaps in the public API surface, standardise timestamps on
`DateTimeOffset`, and establish the reserved `_version` tag that the schema versioning feature writes.
The ExternalMessage routing fields are a small addition but they land as a breaking change because
they touch a persisted table.

| Change | Area | Severity | What broke |
|---|---|---|---|
| TF-1 | Target framework | **High** | net9.0 target removed from all core libraries |
| PS-1..4 | Surface — internal | Medium | `FencingContext`, `ConsistentHashRing`, `FunctionalReactor<T>`, `DeadLetterRetryLoop` made `internal` |
| PS-5..6 | Surface — internal | Medium | `AlbertoStore.FoldWithPosition<TState>`, `ReconstituteWithPosition<TState>` made `internal` |
| PS-7..8 | Surface — deleted | **High** | `IReact<TEvent>` and `AsyncReactor<TReactor>` deleted |
| AS-1 | API shape | Medium | `IStateStore<TState>.LoadManyAsync` return type changed |
| AS-2 | API shape | Low | `IEventProcessor.IsActive` / `IsRebuilding` are now getter-only |
| AS-3..5 | API shape | Medium | Three records lost their positional constructors |
| AS-6 | API shape + migration | **High** | `ExternalMessage` / `OutboxEntry` gain routing fields; migration 027 required |
| DT-1..3 | Timestamps | Medium | Eight timestamp properties changed from `DateTime` to `DateTimeOffset` |
| TV-1..3 | Tag reservation | Low | `EventTag` and `[Tag]` reject any concept starting with `_` |
| MT-1..2 | Metric dimensions | Medium | Sharded-module metric tags split from one combined string into two dimensions |
| MT-3 | Metric dimensions | Medium | Tenant-ownership gauges rename tag `module.key` → `module`; sharded composite key values are now split into `module` + `shard` |
| MT-4..5 | Metric removal | Medium | `alberto.events_filtered_by_tenant` and `alberto.tenant_leases_lost` counters removed |
| MT-6 | Metric units | Low | `alberto.append.duration` and `alberto.processing.duration` units changed from `"ms"` to `"s"` |
| TT-1 | Trace span attributes | Medium | Consume-path span attributes renamed: `"module.key"` → `"module"`, `"module.shard"` → `"shard"` |
| EX-1 | Experimental API | Medium | Sharding types marked `[Experimental("ALB9001")]`; referencing them without suppression is a compile-time diagnostic |
| EV-1 | Evolver — runtime guard | Medium | `Evolver.Reconstitute(envelopes)` and `Evolver.Evolve(state, envelope)` now throw `InvalidOperationException` when the envelope's stored version is older than the handler's declared version |
| PE-1 | ParseEvent&lt;T&gt; removed | **High** | `EventEnvelopeExtensions.ParseEvent<T>` is deleted and the now-empty `EventEnvelopeExtensions` class is `internal` |
| MM-1 | Surface — interface member | Medium | `IMessageMappingRegistry` gains a `ModuleKey` property; direct implementations no longer compile |
| OT-1 | Outbox transport lifecycle | Medium | Failed startup triggers bounded cleanup; store faults stop the relay; shared registrations use one lifecycle |
| VA-1 | Startup validation | Medium | New codes `ALB0018`/`ALB0019`/`ALB0020` reject upcaster misconfigurations that previously started and failed at runtime |
| DL-1 | Surface — interface split | Medium | The three retry-claim members move from `IDeadLetterStore` to a new optional `IClaimableDeadLetterStore` |
| EP-1 | Startup validation | Medium | `ALB0027` — `AddEfProjection` on a `.WithTenancy()` module now requires `documentIds: EfDocumentIdUniqueness.AcrossTenants` |
| SV-1 | Serializer — runtime guard | Medium | `EventSerializer.Deserialize` throws when the stored version is below the type's declared version and nothing covers the gap |
| LK-1 | PostgreSQL append lock | **High** | The append advisory-lock key space changed; two application versions appending concurrently do not serialize against each other |
| CF-1 | Exception detail | Low | PostgreSQL `DcbConflictException` messages are reworded and now carry real `ConflictingPosition` / `ExpectedPosition` / `Query` values |

---

### TF-1 — net9.0 target removed

The seven core libraries (`Alberto.Dcb`, `Alberto.Dcb.Commands`, `Alberto.Dcb.InMemory`,
`Alberto.Dcb.Postgres`, `Alberto.Dcb.EntityFramework`, `Alberto.Dcb.Messaging`,
`Alberto.Dcb.Postgres.Messaging`) previously shipped both `net9.0` and `net10.0` target folders.
They now ship `net10.0` only.

**Symptom.** NuGet resolves the package but the project fails to compile because no compatible
target exists (`error NU1202` or `error NETSDK1138`).

**Fix.** Set `<TargetFramework>net10.0</TargetFramework>` (or add `net10.0` to your
`<TargetFrameworks>` list) in every project that references an Alberto package.

---

### PS-1..4 — `FencingContext`, `ConsistentHashRing`, `FunctionalReactor<T>`, `DeadLetterRetryLoop` made `internal`

These four types were never intended for application use:

- `FencingContext` wraps the token thread-local used by checkpoint fencing. Applications work
  with `IFencedCheckpointStore`.
- `ConsistentHashRing<T>` is the internal shard-routing helper. Applications declare shards with
  `WithTenancy(t => t.AcrossPostgresDatabases(...))`.
- `FunctionalReactor<TEvent>` is the internal wrapper the module builder creates when you call
  `ReactTo<TEvent>(...)`. Use that builder method; do not construct the wrapper yourself.
- `DeadLetterRetryLoop` is the internal hosted service that drives dead-letter retries.

**Symptom.** `error CS0122: '...' is inaccessible due to its protection level`.

**Fix.** Use the public abstractions each type was hiding behind, as described above. If you
have no direct reference, the error will not appear — these types were not meant to be extended
or instantiated from application code.

---

### PS-5..6 — `AlbertoStore.FoldWithPosition<TState>` and `ReconstituteWithPosition<TState>` made `internal`

The position-returning overloads exist so the command pipeline can capture the boundary position
without a second round-trip. Exposing them created a temptation to fold state and note the
position separately from a command pipeline, then pass the captured position to the next
command — a window through which conflicting writes could slip undetected.

```csharp
// before
var (state, position) = await store.FoldWithPosition(query, State.Initial, Apply, ct);

// after — use the single-return overload; the command pipeline handles position capture internally
var state = await store.Fold(query, State.Initial, Apply, ct);
```

If you genuinely need the position for a reason outside the command pipeline — for example
to pass it to a downstream service as a fence — read it from `IEventStoreBackend.GetLastPositionAsync`
rather than from a fold.

---

### PS-7..8 — `IReact<TEvent>` and `AsyncReactor<TReactor>` deleted

`IReact<TEvent>` was the obsolete reactor interface the reflection-based `RegisterReactor` call
used. `AsyncReactor<TReactor>` was its adapter. Both were marked `[Obsolete]` and were never
reachable through any current registration path.

```csharp
// before — required implementing IReact<TEvent>
public class NotificationReactor : IReact<OrderConfirmed>
{
    public Task ReactAsync(OrderConfirmed @event, CancellationToken ct) => ...;
}

// after — a plain method; the module builder wires it
public class NotificationReactor
{
    public Task OnOrderConfirmed(OrderConfirmed @event, CancellationToken ct) => ...;
}

// registration
services.AddAlberto("orders", builder => builder
    .ReactTo<OrderConfirmed, NotificationReactor>(h => h.OnOrderConfirmed));
```

**Symptom.** `error CS0246: The type or namespace name 'IReact<>' could not be found`.

---

### PS-9 — `BatchedEfProjection<TDbContext, THandler>` and `IEfBatchHandler<TDbContext>` deleted

Both were public in `Alberto.Dcb.EntityFramework.Batching` and neither had a registration path —
there was no `AddBatchedEfProjection`, so the only way to use them was to hand-register the
processor into keyed DI. Two consequences followed from that, and both are silent:

- **Rebuilds skip it.** A hand-registered processor produces no `RebuildableProjection`, so
  `RebuildCoordinator` finds no registration for its processor id and passes over it. Once the
  other projections promote to the new state version, the batch projection is still holding
  pre-rebuild state, with nothing raised.
- **A replayed batch is applied twice.** `DeclaredAsyncProjection` skips events at or below
  `IProjectionEntity.LastProcessedPosition`. `BatchedEfProjection` handed the raw `DbContext`
  to the handler with no position guard, so a crash between its `SaveChangesAsync` and the
  control loop's checkpoint write replayed the whole batch — counters incremented twice,
  status transitions re-run, child rows duplicated.

**Symptom.** `error CS0246: The type or namespace name 'IEfBatchHandler<>' could not be found`,
or the same for `BatchedEfProjection<,>`.

**Fix.** Move the handler to a projection declaration and register it with `AddEfProjection`.
The declaration replaces `HandledEventTypes` (it is derived from the `On<TEvent>` calls) and
`ApplyAsync` (each handler gets the typed event and the entity for its document):

```csharp
// before
public sealed class OrderSummaryBatchHandler : IEfBatchHandler<OrdersDbContext>
{
    public IReadOnlySet<string> HandledEventTypes { get; } =
        new HashSet<string> { "order-created", "order-shipped" };

    public async Task ApplyAsync(OrdersDbContext db, IEventEnvelope e, CancellationToken ct)
    {
        // hand-written dispatch on e.EventType.Id, hand-written load of the entity
    }
}

// after
public static readonly ProjectionDeclaration<OrderSummary> Declaration =
    DeclareProjection.For<OrderSummary>("order-summary")
        .On<OrderCreated>(
            id:    e => e.OrderId.ToString(),
            apply: (state, _, _) => state with { Status = "created" })
        .On<OrderShipped>(
            id:    e => e.OrderId.ToString(),
            apply: (state, _, _) => state with { Status = "shipped" })
        .Build();

builder.AddEfProjection<OrderSummary, OrdersDbContext>(Declaration);
```

`DeclaredAsyncProjection`, which `AddEfProjection` registers, is an `IBatchableProcessor` in its
own right: a batch is still one `SaveChanges` per tenant run, so the round-trip saving that
motivated `BatchedEfProjection` is not lost.

If a projection genuinely cannot be expressed as one entity per document — a handler that must
write several unrelated tables from one event — use a reactor (`ReactTo<TEvent, THandler>`) with
its own `DbContext` and its own idempotency key. That is explicit about owning the guard, which
the deleted type was not.

---

### AS-1 — `IStateStore<TState>.LoadManyAsync` return type narrowed

`LoadManyAsync` previously returned `Task<Dictionary<TKey, TState?>>` (a mutable concrete type).
It now returns `Task<IReadOnlyDictionary<TKey, TState?>>`.

```csharp
// before
Dictionary<Guid, OrderState?> states = await store.LoadManyAsync(ids, ct);

// after
IReadOnlyDictionary<Guid, OrderState?> states = await store.LoadManyAsync(ids, ct);
// or, if you need mutation:
var states = (await store.LoadManyAsync(ids, ct)).ToDictionary(...);
```

---

### AS-2 — `IEventProcessor.IsActive` and `IsRebuilding` are now getter-only

These properties describe processor state that the framework sets. Setting them directly from
application code could put the processor into an incoherent state.

**Symptom.** `error CS0200: Property or indexer '...' cannot be assigned to — it is read only`.

**Fix.** Remove the assignment. If you are implementing `IEventProcessor` yourself, remove the
setter from your implementation.

---

### AS-3..5 — positional constructors removed from `DeadLetterEntry`, `ProcessorExecutionOptions`, and `ProjectionStoreContext`

All three records previously exposed a positional constructor whose parameter order became
fragile as the records grew. They are now constructed with named properties only.

```csharp
// before
var entry = new DeadLetterEntry(eventId, processorId, reason, failedAt);

// after
var entry = new DeadLetterEntry
{
    EventId       = eventId,
    ProcessorId   = processorId,
    Reason        = reason,
    FailedAt      = failedAt,
};
```

`ProcessorExecutionOptions` and `ProjectionStoreContext` follow the same pattern.

**Symptom.** `error CS7036: There is no argument given that corresponds to the required parameter`
or `error CS1729: '...' does not contain a constructor that takes N arguments`.

---

### AS-6 — `ExternalMessage` and `OutboxEntry` gain routing fields; migration 027 required

Two properties are added to `ExternalMessage` (and its database projection `OutboxEntry`):

| Property | Type | Purpose |
|---|---|---|
| `Destination` | `string` (required) | The logical routing target — a topic, queue or exchange name |
| `RoutingHint` | `string?` (optional) | An optional hint such as a partition key or routing key |

**Deployment order matters.** Migration 027 adds the two columns to `alberto_outbox_entries`.
Run it **before** deploying the new binary. A binary deployed without the migration will fail at
runtime when it tries to insert outbox rows.

```csharp
// before
new ExternalMessage { Payload = ..., ContentType = "application/json" }

// after
new ExternalMessage
{
    Destination  = "notifications",
    RoutingHint  = order.TenantId.ToString(),
    Payload      = ...,
    ContentType  = "application/json",
}
```

**Symptom.** `error CS9035: Required member '...' must be set in the object initializer` when
constructing `ExternalMessage` without `Destination`.

---

### OT-1 — the outbox owns the complete message-transport lifecycle

Supplying an `IMessageTransport` now transfers responsibility for its start/stop lifecycle to the
outbox. Alberto starts it before claiming or publishing, stops it once after the last relay exits,
and attempts cleanup even when `StartAsync` partially initializes the transport and then throws.
Cleanup receives a cancellation token and Alberto stops waiting after 30 seconds. Alberto still
does not dispose the caller-owned transport instance.

A transport that rejected cleanup unless startup completed will now mask neither failure, but its
`StopAsync` exception is attached to the startup exception's `Data` dictionary:

```csharp
// before — no longer valid: partial startup could allocate _client and then throw
public Task StopAsync(CancellationToken ct) =>
    _started
        ? _client!.CloseAsync(ct)
        : throw new InvalidOperationException("Transport was not started");

// after — cleanup is safe after successful or partial startup
public async Task StopAsync(CancellationToken ct)
{
    if (_client is null)
        return;

    await _client.CloseAsync(ct);
    _client = null;
}
```

Reusing one transport instance in several `WithOutbox` registrations now shares one lifecycle
within that service provider. Those relays may call `PublishAsync` concurrently.

An outbox-store exception outside per-message publishing now faults the relay and closes the
transport instead of being swallowed and retried forever. The host's background-service failure
and restart policy determines recovery.

**Migration steps.**

1. Make `StopAsync` safe when `StartAsync` allocated some resources and then threw.
2. Honor the cleanup cancellation token and keep flushing/closing work within 30 seconds.
3. If one instance is reused across registrations, make `PublishAsync` concurrency-safe or supply
   a separate transport instance to each registration.
4. Confirm the host restarts or terminates as intended when the outbox store becomes unavailable;
   use the store/client's own transient-failure resilience where retry is appropriate.
5. Keep disposing the transport in the application code that constructed it; Alberto only owns
   `StartAsync` and `StopAsync`.

---

### DT-1..3 — `DateTime` and `DateTime?` timestamp properties changed to `DateTimeOffset`

Every timestamp surface in the library that was a `DateTime` is now `DateTimeOffset`. This aligns
with .NET best practices (preserve the UTC offset through serialisation and IANA timezone
conversions), Npgsql's preference for `timestamptz`, and System.Text.Json's default serialisation.

Affected properties:

| Type | Property | Before | After |
|---|---|---|---|
| `IEventEnvelope` | `CreatedAt` | `DateTime` | `DateTimeOffset` |
| `ProcessorInfo` | `UpdatedAt` | `DateTime?` | `DateTimeOffset?` |
| `CheckpointInfo` | `UpdatedAt` | `DateTime?` | `DateTimeOffset?` |
| `DeadLetterInfo` | `FailedAt` | `DateTime?` | `DateTimeOffset?` |
| `ProjectionState` | `UpdatedAt` | `DateTime?` | `DateTimeOffset?` |
| `AdminTenantLease` | `ExpiresAt` | `DateTime?` | `DateTimeOffset?` |
| `ActiveProcessorLease` | `ExpiresAt` | `DateTime` | `DateTimeOffset` |

**Symptom.** `error CS0029: Cannot implicitly convert type 'DateTimeOffset' to 'DateTime'`.

**Fix.** Change local variable declarations from `DateTime` to `DateTimeOffset`. If you persist
these values into a store that requires `DateTime`, call `.UtcDateTime` on the
`DateTimeOffset`. Because all stored values were previously UTC (Npgsql returns UTC for
`timestamptz` columns), the `.UtcDateTime` conversion is exact and lossless.

---

### TV-1..3 — leading-underscore tag concepts reserved

`_version:N` is the tag Alberto writes on every stored event to record its schema version. The
whole `_` prefix — not just `_version` — is reserved, so application code cannot collide with it
now or with any framework tag added later. Reserving only the names in use would make every
future framework tag a breaking change for whoever had already chosen that name; doing it once,
before the API freezes, costs nothing, because domain concepts are things like `order` and
`customer`.

Two construction points enforce the reservation:

```csharp
// both throw ArgumentException:
new EventTag("_version", "1")        // EventTag public constructor — throws at call site
// [Tag("_internal")] on a property  // throws on first append of that event type
```

Boundaries need no separate guard: `DcbQuery` takes `EventTag` values, so a query over a
reserved concept cannot be constructed in the first place.

**Symptom.** `ArgumentException: Concept '_x' starts with '_', which is reserved for
framework-internal tags…` at the construction call site (for `EventTag`), or on
the first append of an event type that carries a leading-underscore `[Tag(...)]` property
(attributes are scanned lazily on first use).

**Fix.** Rename any application tag whose concept starts with `_`. If you are iterating
`IEventEnvelope.Tags` and need to skip framework tags, filter by
`!tag.Concept.StartsWith(EventTag.ReservedConceptPrefix, StringComparison.Ordinal)`.

---

### MT-1..2 — Metric tag shapes for sharded modules corrected

The `alberto.dead_letters`, `alberto.retries`, and `alberto.processor.lag` metrics previously
reported the module key as a single combined string when a module was sharded:

```
module = "orders#shard1"
```

The dimension is now split into two separate tags:

```
module = "orders"
shard  = "shard1"
```

Non-sharded modules are unaffected: their `module` tag keeps the plain module key and no
`shard` tag is emitted.

**Impact.** Any dashboard, alert, or OTel collector configuration that filters or groups on the
combined `module` value for a sharded module will stop matching. Update the filter to use
`module="orders"` and, if you need shard-level isolation, add `shard="shard1"`.

---

### MT-3 — Tenant-ownership gauge tag renamed from `module.key` to `module`

The `alberto.owned_tenant_count` and `alberto.tenant_cooldown_count` observable gauges previously
emitted a tag named `module.key`:

```
consumer.id = "replica-1"
module.key  = "orders"
```

The tag is renamed to `module` (matching every other consume-path instrument, enabling
Prometheus joins without a label transform):

```
consumer.id = "replica-1"
module      = "orders"
```

For sharded modules the raw composite key was previously passed through as-is. It is now split,
consistent with MT-1..2:

```
consumer.id = "replica-1"
module      = "orders"
shard       = "eu"        # only for sharded modules
```

**Impact.** Any Prometheus query, dashboard panel, or alert that references the `module.key`
label on `alberto.owned_tenant_count` or `alberto.tenant_cooldown_count` will stop matching
after upgrade. Rename the label selector to `module`. For sharded modules, also add a `shard`
filter if you need per-shard isolation.

---

### MT-4..5 — `alberto.events_filtered_by_tenant` and `alberto.tenant_leases_lost` counters removed

These two counters were removed from `AlbertoMetrics` in this cycle:

- `alberto.events_filtered_by_tenant` (unit: `"events"`) — counted events skipped due to tenant ownership filtering.
- `alberto.tenant_leases_lost` (unit: `"leases"`) — counted tenant leases lost due to failed renewal.

**Symptom.** Dashboards or alerts that query either counter will return no data after upgrade.

**Fix.** Remove or replace the counter queries. Tenant lease health is reflected in the tenant-ownership gauges (`alberto.owned_tenant_count`, `alberto.tenant_cooldown_count`).

---

### MT-6 — `alberto.append.duration` and `alberto.processing.duration` histogram units changed from `"ms"` to `"s"`

The unit strings on both duration histograms were corrected to match the OTel semantic convention (unit `"s"`, values in seconds):

| Instrument | Old unit | Old value range | New unit | New value range |
|---|---|---|---|---|
| `alberto.append.duration` | `"ms"` | e.g. `5.0` | `"s"` | e.g. `0.005` |
| `alberto.processing.duration` | `"ms"` | e.g. `12.0` | `"s"` | e.g. `0.012` |

**Symptom.** Prometheus histogram bucket thresholds and alerts that assumed millisecond values will fire incorrectly — all durations appear 1000× smaller than expected.

**Fix.** Multiply all threshold values in histogram queries, recording rules, and alert expressions for these two instruments by `0.001`. Verify that any explicit bucket boundaries configured in your OTel SDK exporter also use second-scale values.

---

### TT-1 — Consume-path trace span attributes `"module.key"` → `"module"` and `"module.shard"` → `"shard"`

`TelemetryConsumeMiddleware` and `TelemetryBatchConsumeMiddleware` previously set two span attributes on the consume-path activity:

```
module.key   = "orders"
module.shard = "eu"    # only for sharded modules
```

These are renamed to match the metric tag names established in MT-1..3:

```
module = "orders"
shard  = "eu"          # only for sharded modules
```

**Symptom.** Trace queries, sampling rules, and dashboards that filter on `module.key` or `module.shard` span attributes will stop matching after upgrade.

**Fix.** Rename the attribute key in every trace filter: `span.attributes["module.key"]` → `span.attributes["module"]`; `span.attributes["module.shard"]` → `span.attributes["shard"]`.

---

### TT-2 — `event.tags` on the append span event lists concepts, not `concept:value` pairs

`TelemetryAppendInterceptor` adds one `event.appended` span event per appended event. Its
`event.tags` field was the whole tag, value included:

```
event.tags = "order:8f21-…,customer:4471-…"
```

It is now the distinct concepts only:

```
event.tags = "order,customer"
```

A DCB tag value is a domain identifier — an order id, a customer id, whatever the decision
function scoped its consistency boundary to. Emitting it put a business identifier into every
trace an exporter forwards, for a span that is already explained by the concept: the concept is
what names the boundary the append was checked against.

**Symptom.** Trace-side lookups by entity id ("show me every span touching order 8f21") return
nothing. Spans still show *which* boundaries were involved.

**Fix.** If your collector is inside the same trust boundary as the database, or you are in
development, opt back in:

```csharp
builder.WithTelemetry(o => o with { RecordEventTagValues = true });
```

or through configuration:

```json
{ "Alberto": { "Modules": { "orders": { "Telemetry": { "RecordEventTagValues": true } } } } }
```

Otherwise, correlate on the event id (`event.id`, still emitted in full) and resolve the entity
from the event store.

---

### TT-3 — exception details move from span attributes to an exception span event

Both consume middlewares and the append interceptor set the exception as span attributes:

```
exception.type       = "Npgsql.PostgresException"
exception.message    = "23505: duplicate key value violates unique constraint …"
exception.stacktrace = "   at Npgsql…"
```

All three call sites now call `Activity.AddException(ex)` instead, which records an OpenTelemetry
`exception` span event carrying the same three fields.

The reason is that an exception message is not the framework's data. Npgsql's include the failing
SQL, and application exceptions include whatever the thrower put in them. As span attributes those
strings are indexed alongside every other attribute and there is no unit for a collector to act on;
as a span event they are one droppable, scrubbable record, which is what the OpenTelemetry
processors for redacting exception data expect to find.

`Activity.SetStatus(ActivityStatusCode.Error, ex.Message)` is unchanged — the status description
still carries the message, as OpenTelemetry specifies. The `exception.type` **metric** tag on
`alberto.dead_letters` and `alberto.retries` is also unchanged; it was always the type name only.

**Symptom.** Trace queries and alert rules filtering on the `exception.message`,
`exception.stacktrace`, or `exception.type` *span attributes* stop matching.

**Fix.** Re-point them at the span event. In most backends that is a change of accessor rather
than of field name — for example Tempo/Grafana `span.exception.message` →
`event.exception.message`, and Honeycomb's `exception.message` now resolves on the span event
rather than the parent span. The field names themselves are unchanged.

---

### EX-1 — Sharding types marked `[Experimental("ALB9001")]`

The entire PostgreSQL tenant-sharding surface is now annotated with
`[Experimental("ALB9001", UrlFormat = "...")]`. Any project that references these types without
suppressing the diagnostic will receive a **compile-time warning** (or an error under
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

Affected types:

| Type / method | Assembly |
|---|---|
| `AcrossPostgresDatabases(...)` extension | `Alberto.Dcb.Postgres` |
| `PostgresTenantShardMap` | `Alberto.Dcb.Postgres` |
| `PostgresShardBuilder` | `Alberto.Dcb.Postgres` |
| `ITenantShardMap` | `Alberto.Dcb` |
| `ShardRoutingEventStore` | `Alberto.Dcb` |
| `ShardHealthCheck` | `Alberto.Dcb` |
| `ShardHealth` | `Alberto.Dcb` |
| `ShardExceptions` (`ShardUnroutableException`, `ShardNotFoundException`) | `Alberto.Dcb` |

**Symptom.** `warning ALB9001: '...' is for evaluation purposes only and is subject to change
or removal in future updates.` (or the equivalent error when warnings are treated as errors).

**Fix.** If you are using tenant sharding intentionally and accept the preview stability
guarantee, suppress the diagnostic at the call site or project level:

```csharp
// At the call site
#pragma warning disable ALB9001
.WithTenancy(t => t.AcrossPostgresDatabases(...))
#pragma warning restore ALB9001
```

```xml
<!-- In the .csproj — suppress for the whole project -->
<PropertyGroup>
  <NoWarn>$(NoWarn);ALB9001</NoWarn>
</PropertyGroup>
```

If you are not using tenant sharding, the diagnostic will not fire.

---

### EV-1 — `Evolver.Reconstitute` and `Evolver.Evolve` throw for stale-version envelopes

Calling `Evolver<TState>.Reconstitute(envelopes)` or `Evolver<TState>.Evolve(state, envelope)`
without threading an `EventSerializer` previously silently returned wrong state when the event
was stored at an older schema version than the handler's CLR type expected — raw JSON
deserialization produced a partial or default-filled object instead of the correctly upcasted
shape. These overloads now throw `InvalidOperationException` whenever the envelope's stored
version is less than the version declared by `[EventType(Version = N)]` on the handler type.

**Symptom.** `InvalidOperationException: Event '...' is stored at schema version N but this
evolver handler expects version M. Raw JSON deserialization would produce stale state. Supply
an EventSerializer so the upcaster chain runs before reconstitution: …`

**Fix.** Use the serializer-threaded overloads:

```csharp
// before — silently wrong for stale-version envelopes (now throws)
var state = evolver.Reconstitute(envelopes);

// after — correct; upcaster chain fires before the handler sees the event
var state = evolver.Reconstitute(envelopes, initial: default, serializer.Deserialize);
```

If you go through the command pipeline (`CommandPipeline.Load(boundary, evolver)`) or
`DeciderExtensions.DecideAndAppendAsync` with an `EventSerializer`, upcasting is threaded
automatically and no change is required. The only call sites that need updating are those that
construct an evolver and call its public `Reconstitute` or `Evolve` methods directly, without
a serializer, against a boundary that may contain pre-migration events.

Call sites where all events are guaranteed to be at the current version are unaffected.

The same guard is also applied by the internal `EventEnvelopeExtensions.DeserializeEvent<TEvent>` seam, which is called by `ProjectionDeclaration<TState>.GetDocumentId(IEventEnvelope)` and `ProjectionDeclaration<TState>.Apply(TState, IEventEnvelope, ProjectionContext)`. These two methods are used by testing utilities and standalone projection invocations that bypass the control loop. If you call either with an envelope whose stored version is older than the handler's declared version and no `EventSerializer` is wired for the module, an `InvalidOperationException` is thrown. Wire an `EventSerializer` via `AddUpcaster` (see `docs/events.md`) so that upcasting runs before any projection handler or test helper sees the envelope.

---

### PE-1 — `EventEnvelopeExtensions.ParseEvent<T>` is removed

`EventEnvelopeExtensions.ParseEvent<T>(this IEventEnvelope envelope)` performed raw JSON
deserialization and bypassed any registered upcaster chains. It was never removed after the
upcasting feature landed, leaving a well-named helper that silently did the wrong thing for
any event type that has upcasters.

The method is **deleted**. With it gone, `EventEnvelopeExtensions` has no public members left
and the class itself is now `internal`.

**Symptom.**
```
error CS0117: 'EventEnvelopeExtensions' does not contain a definition for 'ParseEvent'
```
or, for extension-method call syntax:
```
error CS1061: 'IEventEnvelope' does not contain a definition for 'ParseEvent'
```

**Fix.** Replace every call with `EventSerializer.Deserialize(envelope)` followed by a cast:

```csharp
// Before — bypasses upcasters; wrong for any event type with a registered upcaster chain
var order = envelope.ParseEvent<OrderCreated>();

// After — correct; upcaster chain fires before the handler sees the event
var order = (OrderCreated)serializer.Deserialize(envelope);
```

Inject `EventSerializer` from DI (it is registered as a keyed singleton by `WithEventsFrom`
under the module key). If you are inside a consumer registered with `AddProjection`,
`AddEfProjection`, or `ReactTo`, the serializer is already threaded into the pipeline; you
do not need to call `serializer.Deserialize` manually.

There is no suppression escape hatch: a call site that genuinely processes only
current-version events gets the same correct result from `serializer.Deserialize`.

---

### MM-1 — `IMessageMappingRegistry` gains a `ModuleKey` property

Outbox message mappers registered with `Map<TEvent, TMessage>(...)` resolve the module's
`EventSerializer` so that upcasters fire before an event is mapped to an outgoing message.
Previously the mappers resolved an *unkeyed* serializer, which meant that in a host with more
than one module the first module registered won the slot and every other module's mappers
silently used the wrong serializer. The serializer is now resolved with the module key, and the
registry carries that key so the `Map` extension methods keep their existing signatures.

`IMessageMappingRegistry` therefore has a fifth member:

```csharp
string? ModuleKey { get; set; }
```

**Who is affected.** Only code that implements `IMessageMappingRegistry` directly — most
commonly a hand-written test double. Callers of `Map<TEvent, TMessage>(...)` are unaffected;
no call-site signature changed.

**Symptom.**
```
error CS0535: 'TestMappingRegistry' does not implement interface member
'IMessageMappingRegistry.ModuleKey'
```

**Fix.** Add the property to the implementation. Alberto sets it during module registration;
a test double can leave it as an auto-property:

```csharp
public string? ModuleKey { get; set; }
```

A `null` `ModuleKey` resolves no serializer, which means upcasters do not fire on that path — so
if your double is exercising versioned events, set it to the module key under test.

---

### VA-1 — new startup validation for upcaster configuration

Three validation codes now run during the module validation phase. Each rejects a configuration
that previously started successfully and then failed — or silently misbehaved — at runtime.

| Code | Rejects |
|---|---|
| `ALB0018` | An event type declaring `[EventType(Version = N)]` with `N > 1` and no registered upcaster |
| `ALB0019` | An upcaster whose event-type slug is not present in the assembly passed to `WithEventsFrom` |
| `ALB0020` | An upcaster chain that does not terminate at the version the event type declares |

`ALB0018` is the one most likely to fire on upgrade: bumping an event's `Version` without
registering an upcaster used to be accepted at startup and would then write `null` into every
field the new shape added, with no exception. It is now a startup failure.

`ALB0020` catches the near-miss — a chain that exists but stops short:

```csharp
[EventType("order-placed", Version = 3)]        // declares v3
public sealed record OrderPlaced(...) : IEvent;

builder.AddUpcaster(DeclareUpcaster.For<OrderPlaced>("order-placed")
    .From<OrderPlacedV1>(1, v1 => ...)          // chain reaches v2 only
    .Build());
```

Previously this started cleanly and threw only when a v1 or v2 event was actually read, which
can be days after the deploy. The remedy in the failure message names the missing step.

The mirror mistake is also rejected: a chain that was extended while the attribute was left
behind. That one never throws at all — events keep being written at the old version and every
read runs the whole chain forever — so `ALB0020` is the only thing that will ever tell you.
This applies at version 1 too, so a `[EventType("x")]` with no explicit `Version` and a
one-step chain is now a startup failure; raise the attribute to match the chain.

These checks only run when `WithEventsFrom(...)` was called; a module configured without an
events assembly skips them.

---

### DL-1 — retry claims move to `IClaimableDeadLetterStore`

Three members left `IDeadLetterStore`:

```csharp
Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
    string processorId, int batchSize, TimeSpan leaseDuration, string claimedBy, CancellationToken ct = default);
Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);
Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default);
```

They now sit on `IClaimableDeadLetterStore`, which extends `IDeadLetterStore` and is type-tested
for at the point of use.

**Why.** These three are one operation — claim a batch under a fence, then release or complete it
— and it has no correct default implementation. A store backed by a table can do it with
`FOR UPDATE SKIP LOCKED`; a store backed by an append-only file, a log shipper, or an HTTP
endpoint cannot, and had to implement three methods it could only throw from. That is the shape
that says the capability is optional, not that the interface is incomplete.

**Symptom.** `error CS0535: '...' does not implement interface member` disappears for
implementors; `error CS1061: 'IDeadLetterStore' does not contain a definition for
'ClaimRetryRequestedAsync'` appears for callers.

**Fix — implementing a store.** Both shipped stores (`PostgresDeadLetterStore`,
`InMemoryDeadLetterStore`) now declare `IClaimableDeadLetterStore`, so nothing changes for them.
For a custom store, keep the three methods and move the declaration up:

```csharp
public sealed class MyDeadLetterStore : IClaimableDeadLetterStore { ... }  // was IDeadLetterStore
```

If your store threw `NotSupportedException` from them, delete them and implement
`IDeadLetterStore` alone. It still records, counts, reads and clears dead letters; what it loses
is the automatic retry loop. Alberto logs a warning naming the store type at startup —
`alberto ops deadletter retry` will still flag entries and nothing will dispatch them, so the
warning is there rather than leaving an operator watching a flag that never clears.

**Fix — calling the methods.** Type-test:

```csharp
if (store is IClaimableDeadLetterStore claimable)
    await claimable.ClaimRetryRequestedAsync(processorId, 10, TimeSpan.FromMinutes(1), "me", ct);
```

**Fix — the conformance suite.** `DeadLetterStoreSpecification` keeps the core requirements and
its `CreateStore()` still returns `IDeadLetterStore`. The claim requirements moved to
`ClaimableDeadLetterStoreSpecification`, which derives from it and adds one abstract member:

```csharp
public sealed class MyDeadLetterStoreTests : ClaimableDeadLetterStoreSpecification
{
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() => ...;
}
```

A store that is not claimable derives from `DeadLetterStoreSpecification` as before.

**Related.** `ExtensionPointContractTests` now freezes the abstract member set of every interface
implemented outside this repository, so a member added after 1.0 either ships with a default
implementation or moves to its own optional interface, as this one did. See
[CONTRIBUTING.md](CONTRIBUTING.md).

---

### EP-1 — `ALB0027`: EF projections on a tenant-enabled module must declare id uniqueness

An EF projection entity implements `IProjectionEntity`, whose whole surface is `DocumentId`,
`UpdatedAt`, `LastProcessedPosition`, `Version` and `RebuildVersion`. There is no tenant column,
so `EfStateStore` and the inline path both load and write by `(DocumentId, RebuildVersion)` alone.
Adding a `TenantId` column to your entity does not change that — the store cannot see it.

On a module that declared `.WithTenancy()`, the projection is therefore only correct if two
tenants can never produce the same document id. Nothing can verify that, so it now has to be
stated:

```csharp
services.AddAlberto("orders", m =>
{
    m.WithPostgres(cs).WithTenancy();

    m.AddEfProjection<OrderSummaryEntity, OrdersDbContext>(
        declaration,
        documentIds: EfDocumentIdUniqueness.AcrossTenants);   // ← required
});
```

**Symptom.** `InvalidOperationException` at `AddAlberto` with `ALB0027`, naming the processor id.
The check runs from the deferred registration callback, so it fires whether `.WithTenancy()` is
chained before or after the projection.

**Fix.** Decide which is true of your declaration:

- **Ids are already globally unique** — a GUID aggregate id, or an id the handler prefixes with a
  tenant discriminator carried on the event itself. Pass
  `documentIds: EfDocumentIdUniqueness.AcrossTenants`. Nothing else changes.
- **Ids are per-tenant** (`"order-42"`) — two tenants share one row today, and have been. Either
  make the id carry the tenant, give the module `.WithTenancy(t => t.AcrossPostgresDatabases(...))`
  so each tenant has its own database, or move the projection to `AddProjection` with a JSONB
  state store, which is tenant-scoped by the store.

Single-tenant modules are unaffected: `EfDocumentIdUniqueness.NotDeclared` stays the default and
stays correct, because there is no second tenant to collide with.

See [docs/projections.md](docs/projections.md#ef-projections-on-a-tenant-enabled-module).

---

### SV-1 — `EventSerializer.Deserialize` refuses an uncovered version gap

Reading an envelope stored below the version its CLR type declares now throws
`InvalidOperationException`:

```
Event 'order-placed' is stored at schema version 1, but 'OrderPlaced' declares
[EventType(Version = 2)]. No upcaster is registered for it, so the stored payload would be
deserialized straight into the current shape and every member added since version 1 would
silently take its default value.
```

**Why.** `ALB0018` already rejects this at startup — but only on the DI path. A serializer built
by hand (`EventSerializer.FromAssembly(...)` in a migration script, a test helper, a one-off
backfill tool) never meets the validator, and the failure it lets through is silent:
System.Text.Json leaves every member the older payload lacks at its CLR default, so a
non-nullable `string Region` comes back `null` and an evolver folds it as though it had been
stored. The guard is now on the serializer itself, where it cannot be bypassed.

It also catches the case `ALB0020` covers — a chain that terminates below the declared version —
when the envelope sits exactly at the chain's own current version and no step fires.

**Symptom.** A tool or test that read old events now throws where it used to return an object
with default-valued members.

**Fix — the normal case.** Register the upcaster the version bump needed:

```csharp
DeclareUpcaster.For<OrderPlaced>("order-placed")
    .From<OrderPlacedV1>(1, v1 => new OrderPlaced(v1.OrderId, v1.Amount, "eu-west-1"))
    .Build();
```

**Fix — the escape hatch.** If the bump only added optional members whose defaults are already
the values you want for events written before it, say so at the declaration site:

```csharp
[EventType("order-placed", Version = 2, UpcastingNotRequired = true)]
public sealed record OrderPlaced(Guid OrderId, decimal Amount, string Region = "eu-west-1") : IEvent;
```

It is a claim about the JSON, not about the C#: the property default has to be right for every
event already in the log. `ALB0018` honours the same flag, so a waived gap does not fail startup
either.

**Not guarded:** reading an envelope *newer* than the running code. System.Text.Json ignores
members it does not know about, which is what makes a rolling deploy survivable. Only the
downward gap fabricates values.

---

### LK-1 — the PostgreSQL append advisory-lock key space changed

The append lock moved from the two-argument form to the single-argument one:

```sql
-- before
SELECT pg_advisory_xact_lock(1, hashtext('alberto-append:public:tenant-a'));
-- after
SELECT pg_advisory_xact_lock(hashtextextended('alberto-append:public:tenant-a', 0));
```

**Why.** The old form fixed classid at 1, so every distinct key had to fit in `hashtext`'s 32-bit
output. Collisions there are not a correctness problem — a shared lock over-serializes, it never
under-serializes — but by the birthday bound P(some pair of tenants collides) ≈ 1 − exp(−n²/2³³),
which is ~0.6% at 10k tenants, 10% at 30k and 50% at 77k. The symptom is two unrelated tenants'
appends serializing against each other with no error, no log line and nothing in telemetry to
point at it. `hashtextextended` returns `bigint`, moving the same bound to under 1 in 10¹⁰ for a
million tenants.

**No schema change and no migration.** Advisory locks live in shared memory, not in a table.

**Rollout — this is the part that matters.** Two application versions appending to the *same*
database take locks in *different* key spaces and therefore do not serialize against each other.
For the length of the overlap the DCB conflict check is unprotected, and two concurrent appends
can both pass a boundary check that should have rejected one of them.

- **Drain or stop the old version before starting the new one.** A brief write outage on that
  database is the safe rollout.
- A rolling deploy with both versions live and appending is **not** safe. If your platform will
  not let you stop writes, scale the old deployment to zero replicas first, then deploy.
- Readers, projections, the outbox relay and the CLI are unaffected — none of them take the
  append lock.
- Nothing is corrupted by a *past* collision, so there is nothing to repair afterwards.

The migration lock (`hashtext('alberto:migrate')`) now shares a namespace with append keys. An
append key would have to hash to exactly that one fixed value — a 2⁻⁶⁴ event — and the
consequence if it ever did is an append waiting behind a schema migration, which is the desired
order regardless.

---

### CF-1 — PostgreSQL `DcbConflictException` carries real details

Every conflict raised by the PostgreSQL backend used to report `ConflictingPosition` and
`ExpectedPosition` as `-1` and `Query` as `DcbQuery.Empty` — which renders as `*`, i.e.
indistinguishable from a genuine conflict at position -1 against an all-events query. The backend
was throwing the `DcbConflictException(message, inner)` overload while holding the query and the
expected position it had just been given.

All three now carry real values. The conflicting position is parsed out of the server's
`RAISE EXCEPTION` message, and the server's own wording — which names *which* arm of the boundary
matched — is kept inside the composed message rather than replaced.

**Symptom.** Code matching on the old message text
(`"DCB conflict detected: events matching the query exist after the expected position"`) no
longer matches. Logs and error strings differ.

**Fix.** Read the properties instead:

```csharp
catch (DcbConflictException ex)
{
    logger.LogWarning("Conflict at {Position}, expected {Expected}, query {Query}",
        ex.ConflictingPosition, ex.ExpectedPosition, ex.Query);
}
```

`RetryOnConflict(n)` and `TryCommit` are unaffected — they never read the message.

For custom backends, prefer the new
`DcbConflictException(string, long, long, DcbQuery, Exception)` constructor wherever the details
are knowable. The two-argument `(string, Exception)` overload still exists for the case where a
backend genuinely cannot say, and its documentation now spells out that the `-1`/`*` it produces
are placeholders rather than facts.

---

## The command pipeline is reshaped; `Persist` is now `Commit`

The fluent pipeline in `Alberto.Dcb.Commands` was rebuilt around one idea: **the boundary should
decide which terminal you get.** Previously every pipeline exposed `Persist(query, position, ct)`,
`PersistUnconditionally(ct)` and a `Persist(ct)` that threw at runtime when no boundary had been
established. Now the type you are holding tells you which of those is legal.

| Change | Severity | What broke |
|---|---|---|
| P-1 | **High** | `Persist` renamed to `Commit`; `PersistUnconditionally` to `CommitUnconditionally` |
| P-2 | **High** | `NoValidation()` removed — validation was always optional |
| P-3 | **High** | `WithEventsFrom` registers `AlbertoStore` **keyed** by module key |
| P-4 | Medium | `Decide` is synchronous; use the new `Enrich` stage for async work before the boundary |
| P-5 | Medium | The three-lambda `Load(loader, querySelector, positionSelector)` is replaced by `LoadUnder` |

### P-1 / P-2 — the pipeline shape

```csharp
// before
await store.Handle(command)
    .NoValidation()
    .Load(boundary, initial, apply)
    .Decide((cmd, state) => …)
    .Persist(ct);

// after
await store.Handle(command)
    .Load(boundary, initial, apply)
    .Decide((cmd, state) => …)
    .Commit(ct);
```

`Validate` is now genuinely optional — drop `NoValidation()` and nothing replaces it. It is also
chainable, and short-circuits on the first failure without reading the log.

`Load` returns a **bound** pipeline, and only a bound pipeline has `Commit(ct)`. Skipping `Load`,
or using the new `LoadUnbound` for state that does not come from the log, gives you an **unbound**
pipeline whose terminals are `Commit(query, expectedPosition, ct)` and `CommitUnconditionally(ct)`.
The runtime "no boundary was observed" exception is gone because that state is now unrepresentable.

Two terminals are new. `TryCommit` returns a failed `Result` carrying a `dcb.conflict` problem
instead of throwing `DcbConflictException`. `RetryOnConflict(n)` bounds the total attempts,
re-running `Load` and `Decide` against the current log on each one — stages before `Load` are
memoized and run exactly once.

### P-3 — `AlbertoStore` is keyed

```csharp
// before
var store = sp.GetRequiredService<AlbertoStore>();

// after
var store = sp.GetRequiredKeyedService<AlbertoStore>("orders");
```

`IEventStore` was already keyed by module key. `AlbertoStore` was not, so a host registering two
modules got one store — whichever registered last — wrapping the wrong log. `WithEventsFrom` now
registers it with `AddKeyedScoped` under the same key, so each module gets its own.

The store is also handed the service provider, so `Load<TState>(boundary)` can resolve
`Evolver<TState>` from DI instead of taking one as an argument. Constructing an `AlbertoStore`
yourself still works; that overload then throws with a message telling you to pass the evolver
explicitly.

### P-4 — `Decide` is synchronous, `Enrich` is where async goes

`Decide` used to accept an async delegate, which put arbitrary I/O *inside* the window between
reading the boundary and appending under it — the one place where latency turns directly into
conflicts. It is now synchronous.

Work that needs to be awaited moves to `Enrich`, which runs before `Load`:

```csharp
await store.Handle(command)
    .Enrich(async (cmd, ct) => cmd with { Rate = await rates.GetAsync(cmd.Currency, ct) })
    .Load(cmd => Boundary(cmd.OrderId), evolver)
    .Decide((cmd, state) => Actions.Convert(state, cmd.Rate))
    .RetryOnConflict(3)
    .Commit(ct);
```

`Enrich` may change the command's type, and it runs once even when `RetryOnConflict` re-reads.

### P-5 — a custom loader now reports its boundary through `LoadUnder`

The old three-lambda overload let an async loader declare the boundary it had observed:

```csharp
// before
.Load(cmd => LoadAsync(store, cmd, ct), loaded => loaded.Query, loaded => loaded.Position)
.Decide((cmd, loaded) => Decide(loaded.State, …))
.Persist(ct);
```

That shape does not survive the bound/unbound split — the unbound terminal's arguments are
evaluated *before* the deferred chain runs, so a loader cannot supply them. `LoadUnder` restores
the capability with the type-state guarantee intact: the loader returns its state, its boundary
and the position it read at, and the pipeline it produces is **bound**, so `Commit(ct)` checks
against exactly that.

```csharp
// after
.LoadUnder(async (cmd, ct) =>
{
    var boundary = BuildBoundary(cmd);
    // Use IEventStore.StreamAsync directly: fold state and track the max position in one pass.
    var envelopes = await eventStore.StreamAsync(boundary, cancellationToken: ct);
    var state = envelopes.Aggregate(State.Initial, (s, e) => Apply(s, serializer.Deserialize(e)));
    var position = envelopes.Count > 0 ? envelopes.Max(e => e.GlobalPosition) : 0L;
    return (state, boundary, position);
})
.Decide((cmd, state) => Decide(state, …))
.Commit(ct);
```

Most call sites do not need it. If the boundary is merely *derived from the command*, prefer
`Load(cmd => boundary, initial, apply)`, and put any async work in `Enrich` so it lands before the
window opens — that combination is both shorter and strictly safer, since the I/O then sits outside
the read-to-append gap. `LoadUnder` is for the case those cannot express: a boundary that is only
discoverable **during** the load, such as folding one query to find an id and then folding a second
keyed by it.

---

## Deprecated projection and decision APIs removed

**Breaking.** Every type that carried `[Obsolete]` has been deleted, along with the reflection-based
projection stack that only those types reached. Nothing here had a runtime replacement pending — the
blessed spelling has shipped for a full cycle in each case, so calls now fail to compile rather than
warn.

| Removed | Replacement |
|---|---|
| `DecisionResult<TEvent>` (and `.Ok` / `.Fail` / `Success()` / `Failure()` / `EnsureSuccess()`) | `Decision` / `Decision<T>` + `Problem` |
| `Projection<TState>` base class | `DeclareProjection.For<TState>(...)` → `ProjectionDeclaration<TState>` |
| `IProject<TState, TEvent>` | `.On<TEvent>(id:, apply:)` on the declaration builder |
| `ProjectionDispatcher<TState>` | (internal) delegate dispatch inside `ProjectionDeclaration<TState>` |
| `AsyncProjection<TState, TProjection>` | (internal) `DeclaredAsyncProjection<TState>` |
| `InlineProjection<TState, TProjection>` | (internal) — see *inline projections* below |
| `EfInlineProjection<TEntity, TProjection, TDbContext>` | (internal) `DeclaredEfInlineProjection<TEntity, TDbContext>` |
| `RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(...)` | `AddEfProjection<TEntity, TDbContext>(declaration, ProjectionMode.Inline)` |
| `IEventStoreConfigurator.RegisterInlineProjection<TState, TProjection>(IStateStore<TState>)` | `RegisterInlineProjection(IInlineProjection)` |
| `EventConsumerExtensions.RegisterProjection<TState, TProjection>(...)` | `AddProjection<TState>(declaration, stateStoreFactory)` on the module builder |
| `IEventConsumer` and `EventConsumerExtensions` (incl. `RegisterReactor<TReactor>`) | `ReactTo<TEvent>(...)` / `ReactTo<TEvent, THandler>(...)` on the module builder |

### Event consumers

`IEventConsumer` had no implementation anywhere in the library — it described a processor-routing seam
that `ControlLoop` ended up filling with a different shape. Its only extension methods were the two
`Register*` calls above, so an application could not reach it without writing the consumer itself.

Reactors are registered on the module builder, which resolves the handler from DI and needs no
interface on the reactor type:

```csharp
// Before — required an IEventConsumer implementation that the library never shipped
consumer.RegisterReactor(new NotificationReactor(...));   // reactor implements IReact<TEvent>

// After
services.AddAlberto("orders", builder => builder
    .ReactTo<OrderConfirmed, NotificationReactor>(h => h.OnOrderConfirmed)
);
```

`IReact<TEvent>`, `AsyncReactor<TReactor>` and `ReactorDispatcher` — the reflection-dispatched reactor
path that `RegisterReactor` built — are still present but are no longer reachable from any registration
API. `ReactTo` uses `FunctionalReactor<TEvent>` / `SyncReactor<TEvent>` and binds the handler method
explicitly rather than scanning for `IReact<TEvent>` interfaces.

### Decisions

`Problem.Create(code, message)` takes a kebab-case code alongside the message, so failures carry a
stable identifier instead of only prose:

```csharp
// Before
public static DecisionResult<IEvent> Create(...)
{
    if (alreadyExists) return DecisionResult<IEvent>.Failure("Order already exists");
    return DecisionResult<IEvent>.Success(new OrderCreated(...));
}

// After
public static Decision Create(...)
{
    if (alreadyExists) return Problem.Create("order-already-exists", "Order already exists");
    return Decision.Succeed(new OrderCreated(...));
}
```

`Decision` lives in `Alberto.Dcb.Commands` (namespace `Alberto.Dcb`), so a project that previously
only referenced `Alberto.Dcb` for `DecisionResult<TEvent>` needs a reference to
`Alberto.Dcb.Commands` as well. The two example `Core` projects gained exactly that.

`EnsureSuccess()` has no replacement: `Decision` exposes `IsError` / `Problems`, and the caller
decides how a failure surfaces (an exception, a GraphQL error, an HTTP problem detail) rather than
having `InvalidOperationException` chosen for it.

### Projections

Projections are declared rather than inherited. The old base class discovered handlers by scanning
`IProject<,>` interfaces at construction time; the declaration binds the document-ID selector and
the fold together per event type, with no reflection at runtime:

```csharp
// Before
public class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderPlaced>
{
    public string GetDocumentId(OrderPlaced e) => e.OrderId.ToString();
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderPlaced e, ProjectionContext ctx)
        => state with { Total = e.Total };
}

// After
public static readonly ProjectionDeclaration<OrderSummary> Declaration =
    DeclareProjection.For<OrderSummary>("order-summary")
        .On<OrderPlaced>(
            id: e => e.OrderId.ToString(),
            apply: (state, e, ctx) => state with { Total = e.Total })
        .Build();
```

`ProjectionResult<TState>`, `ProjectionResults`, `ProjectionContext`, `IStateStore<TState>` and
`IInlineProjection` are unchanged — only the way handlers are declared changed.

One behavioural difference worth knowing: `Projection<TState>.Apply` returned `Unchanged` for an
event it did not handle, while `ProjectionDeclaration<TState>.Apply` throws for an unregistered
event type. Processors filter on `HandledEventTypes` before dispatching, so reaching `Apply` with an
undeclared event is a wiring bug rather than something to swallow.

### Inline projections

`Projection<TState>` was the only way to run a **non-EF** inline projection: `InlineProjection<,>`
wrapped it, and `RegisterInlineProjection<TState, TProjection>(IStateStore<TState>)` was the only
entry point. Neither has a declaration-based equivalent — `AddProjection<TState>` wires the async
path only — so the non-EF inline path is gone with them.

Two replacements, depending on what the inline write is for:

- **EF entities:** `AddEfProjection<TEntity, TDbContext>(declaration, ProjectionMode.Inline)`, which
  enlists in the appending `DbContext` transaction.
- **Anything else:** implement `IInlineProjection` directly and register it with
  `configurator.RegisterInlineProjection(projection)`. The interface is two members
  (`HandledEventTypes` and `ProcessAsync`) and remains public for exactly this.

---

## `AddAlbertoStore` removed — use `WithEventsFrom`

**Breaking.** `AddAlbertoStore` is gone in both forms. There is no deprecation window: the name no
longer exists, so calls fail to compile rather than warn.

| Removed | Replacement |
|---|---|
| `builder.AddAlbertoStore(assembly)` | `builder.WithEventsFrom(assembly)` |
| `services.AddAlbertoStore(moduleKey, assembly)` | `builder.WithEventsFrom(assembly)` inside the `AddAlberto` callback |

`services.AddAlberto(...)` and `builder.AddAlbertoStore(...)` read as two halves of one bootstrap
step, as if the first call left the module half-configured. They were not. `AddAlberto` builds the
module; `AddAlbertoStore` came from the separate, optional `Alberto.Dcb.Commands` package and did
one thing — declare where the module's `[EventType]` records live, and register the `AlbertoStore`
command pipeline over them. That is module configuration, so it now reads like the other
one-per-module settings (`WithPostgres`, `WithControlLoop`, `WithTenancy`) rather than like a
second registration step. `Add*` stays reserved for the N-of-a-kind calls (`AddProjection`,
`AddReactor`).

```csharp
// Before
services.AddAlberto("orders", builder => builder
    .WithPostgres(...)
    .AddAlbertoStore(typeof(OrderCreated).Assembly));

// After
services.AddAlberto("orders", builder => builder
    .WithPostgres(...)
    .WithEventsFrom(typeof(OrderCreated).Assembly));
```

The standalone `services.AddAlbertoStore(moduleKey, assembly)` overload — `[Obsolete]` since the
2026-07-24 audit cycle — is removed in the same change rather than left behind as the only
surviving spelling of a name this cycle retires.

The containing class was renamed to match what it now extends:
`AlbertoStoreServiceCollectionExtensions` → `AlbertoStoreBuilderExtensions`
(`ServiceCollectionExtensions.cs` → `AlbertoStoreBuilderExtensions.cs`). This only affects code
that called the method non-extension style; ordinary `.WithEventsFrom(...)` chaining is unaffected.

---

## Core and operator correctness hardening

This cycle makes five previously implicit invariants explicit:

- PostgreSQL schema identifiers must be blank (meaning `public`) or match
  `^[a-z_][a-z0-9_]*$`. Invalid runtime and CLI schemas now fail with `ALB1005` instead of
  reaching interpolated SQL.
- `PostgresAdminDataAccess.GetEventsAsync` and `GetDeadLettersAsync` gained a nullable `tenant`
  argument, and their result records expose `TenantId`. `ProjectionState.TenantId` is now nullable
  because a single-tenant store has no tenant column.
- `TenantLeasesTableExistsAsync` and `GetTenantLeasesAsync` were replaced by
  `GetTenantLeaseInventoryAsync`. The returned `TenancyMode` distinguishes a single-tenant store
  from an empty multi-tenant lease inventory.
- A pipelined processor fault now faults the control loop without advancing its checkpoint past
  the failed event. Hosts should continue treating `ControlLoop.IsFaulted` as a restart signal.
- `EfStateStore.ApplyChangesAsync` now commits the complete upsert/delete batch atomically and
  throws `ConcurrencyConflictException` after bounded conflict retries. It no longer falls back to
  partial per-entity saves or reports success after swallowing write failures.

The admin CLI detects the migrated tenancy topology automatically. `events` and `dead-letters`
accept `--tenant`; using a tenant filter against a single-tenant store fails explicitly.

---

## Command decisions now require an observed DCB position

`DecidedPipeline.Persist(DcbQuery, CancellationToken)` has been replaced by
`Persist(DcbQuery, long expectedPosition, CancellationToken)`. Supplying a query without the
position at which it was observed silently disabled the DCB conflict check.

For commands that intentionally append without a conflict check, call
`PersistUnconditionally(CancellationToken)`. The `Load(load, query)` overload that could create
an unobserved boundary has been removed; use `Load(load, query, expectedPosition)` instead.

---

## Summary — outbox claim leases and atomic admin mutations

Two operator-facing lifecycles now sit behind one tested interface each.

| Change | Area | Severity | What broke |
|---|---|---|---|
| CL-1 | Outbox | **High** | `IOutboxStore.GetPendingAsync` became `ClaimPendingAsync`; completion takes an `OutboxClaim` and returns `bool` |
| CL-2 | Schema | **High** | Outbox rows gain `claim_id`, `claimed_by`, and `claim_expires_at` through migration 016 |
| CL-3 | Admin | Low | `PostgresAdminDataAccess.SetCheckpointAsync` / `ResetCheckpointAsync` were replaced by atomic `RenameCheckpointAsync` |

### CL-1 — outbox claims are leased and token-fenced

Custom `IOutboxStore` adapters must implement the claim lifecycle:

```csharp
Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
    int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct);
Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct);
Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct);
```

Expired `processing` entries are eligible for reclaim. Completion returns `false` when the token
no longer owns the row, preventing a stale relay from overwriting a newer claim. `WithOutbox`
accepts `relayClaimLease`; its default is five minutes.

### CL-2 — apply migration 016

Migration 016 adds the outbox claim token, diagnostic owner, expiry, and expired-claim index.
Existing `processing` rows have no expiry and are therefore recoverable immediately after upgrade.

### CL-3 — checkpoint rename is one transaction

Call `PostgresAdminDataAccess.RenameCheckpointAsync(from, to)`. It returns a
`CheckpointRenameResult` and never overwrites an existing destination. The CLI uses this method;
the old public set/reset primitives were removed because they allowed callers to split rename
across several connections.

---

## Summary — projection rebuild cycle

Zero-downtime projection rebuilds landed. Projection state is now versioned, which is a
source-breaking change for anyone registering projections and a schema change for anyone
using `AddEfProjection`.

| Change | Area | Severity | What broke |
|---|---|---|---|
| RB-1 | Projections | **High** | `AddProjection` takes a `ProjectionStoreContext`, not an `IServiceProvider` |
| RB-2 | EF projections | **High** | Projection entities need a `(DocumentId, RebuildVersion)` key — schema change |
| RB-3 | Rebuilds | Medium | `IProjectionStateClearer.ClearAsync` → `ClearVersionAsync(int, ct)` |
| RB-4 | CLI | Low | `alberto ops rebuild` is now a parent command with subcommands |

### RB-1 — `AddProjection` hands you a context, not a provider

```csharp
// before
.AddProjection(decl, sp =>
{
    var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
    return () => new PostgresStateStore<OrdersOverview>(dataSource, "OrdersOverview", "orders");
})

// after
.AddProjection(decl, ctx =>
{
    var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
    return () => new PostgresStateStore<OrdersOverview>(
        dataSource, "OrdersOverview", "orders", rebuildVersion: ctx.RebuildVersion);
})
```

`ctx.Services` is the old parameter. `ctx.RebuildVersion` is a `Func<int>` the store resolves on
every operation — pass it through rather than calling it once, because a promotion has to take
effect underneath a store that is already running.

Passing it is optional. A store that ignores it keeps working exactly as before; it just cannot
be rebuilt without downtime. `AddProjection` also gained an optional `projectionType` parameter
for projections whose state rows are keyed by something other than the processor id.

**Why:** the same factory has to build both the live projection and the shadow copy a rebuild
replays into. The only difference between them is which version they write to, so that is what
the context carries.

#### Stores built outside the module builder

`ProjectionStoreContext` only exists inside an `AddProjection` factory. Code that constructs a
state store elsewhere — a query handler, a GraphQL resolver — has no context to draw a version
from, and a store left on the default pins itself to version 1 and keeps serving the *pre-rebuild*
copy forever after a promotion.

`ProjectionVersions.LiveVersion` is the reader-side entry point:

```csharp
new PostgresStateStore<OrdersOverview>(
    dataSource,
    projectionType: nameof(OrdersOverviewProjection),
    schema: "orders",
    rebuildVersion: ProjectionVersions.LiveVersion(sp, ModuleKey, nameof(OrdersOverviewProjection)));
```

It resolves to version 1 forever in a module with no rebuild pipeline, so it is safe to use
unconditionally.

### RB-2 — EF projection entities are keyed by `(DocumentId, RebuildVersion)`

Configure every entity registered with `AddEfProjection` in `OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ProjectionEntity<OrderSummaryEntity>(entity =>
    {
        entity.ToTable("order_summaries");
        entity.Property(e => e.CustomerName).HasMaxLength(200);
    });
}
```

This makes the key composite and defaults `RebuildVersion` to `1`, so existing rows read as
version 1 and nothing moves. **Generate and apply an EF migration for it** — it is a primary-key
change plus a new index.

Anything that called `FindAsync` on a projection entity with one key value now needs two:

```csharp
await context.Counters.FindAsync([documentId, ProjectionVersions.Initial], ct);
```

**Why:** without the version in the key, the shadow rebuild's rows collide with the live rows on
insert, and the rebuild silently overwrites the projection it was supposed to be shadowing.

### RB-3 — `IProjectionStateClearer` clears one version

`ClearAsync(ct)` is now `ClearVersionAsync(int rebuildVersion, ct)`. A rebuild cannot truncate
the table: the other version is live and being read. Implementations must filter on the version.

`EfProjectionStateClearer` is registered automatically by `AddEfProjection`; only hand-written
implementations need changing.

### RB-4 — `alberto ops rebuild` gained subcommands

`alberto ops rebuild <processor>` used to reset the checkpoint and nothing else. It is now:

```bash
alberto ops rebuild start <processor> [--projection-type <type>] [--dry-run] [--yes]
alberto ops rebuild status [processor]
alberto ops rebuild promote <processor> [--force]
alberto ops rebuild abort <processor>
```

The replay runs in the application, not in the CLI. A module must opt in with
`.WithRebuilds()` or a started rebuild sits at `rebuilding` forever.

### Also removed

`BufferedCheckpointStore` is gone. It was `internal` and never constructed, so no consumer can be
affected; `CachingCheckpointStore` is and always was the one in the pipeline.

---

## 0.x → 1.0: declarative configuration pipeline

### ⚠ Read this first: processor ids are checkpoint keys

**The `ReactTo<TEvent, THandler>` change below is the only change that can silently
reprocess your entire event log.** When `processorId` is omitted, Alberto now derives the
checkpoint key from the handler's type name via `ProcessorId.For<THandler>()` — reading a
`[ProcessorId]` attribute when present, otherwise building a qualified name from the type
hierarchy. If that derived name differs from what was stored in your checkpoint table, the
processor restarts from position zero without warning.

Alberto's safety net is `Checkpoints:OrphanPolicy`. Outside a `Development` environment,
it now defaults to `Strict`, which causes startup to fail with a named error if the
checkpoint store contains an id that no declared processor claims — turning a silent replay
into a loud failure. See the `OrphanPolicy` row below for the asymmetry between code and
configuration, and [docs/configuration.md](docs/configuration.md#checkpoint-hygiene) for
the configuration key.

**Before deploying 1.0:**

1. Audit every `ReactTo<TEvent, THandler>` call in your modules.
2. Compare the derived id (the handler class name, qualified by any declaring types) with
   the id stored in your checkpoint table.
3. If they differ, either add `[ProcessorId("old-id")]` to the handler class, or rename
   the checkpoint with `alberto ops checkpoint rename`.

---

### Breaking changes table

| Change | What breaks | What to do |
|---|---|---|
| `DcbModuleBuilder.Services` removed | Third-party `.WithX()` extensions that reached into the service collection at declaration time | Implement `IAlbertoBackendDescriptor` for a backend; use `builder.Register(context => ...)` for anything else |
| `Action<TOptions>` → `Func<TOptions, TOptions>` on `WithPostgres` | Every call site | `o => { o.X = y; }` becomes `o => o with { X = y }` |
| `PostgresOptions` is a record | Object initializers still work; assignment after construction does not | Use `with` expressions to derive a new value |
| `ControlLoopBuilder` deleted | `.WithPollingInterval(...)` and siblings on the old builder | `WithControlLoop(o => o with { PollingInterval = ... })` — see [ControlLoop options](docs/configuration.md#controlloop-options) |
| `.WithMiddleware(...)` / `.WithBatchMiddleware(...)` removed | Control-loop-scoped middleware registration | Module-level `AddConsumeMiddleware(sp => ...)` / `AddBatchConsumeMiddleware(sp => ...)` |
| `ErrorPolicy` split | Custom classifiers | Retry knobs move to `ControlLoop.Retry`; the classifier moves to `UseErrorClassifier<T>()` |
| `ProcessorExecutionConfigurator` deleted | `configure: c => c.BatchIfSupported()` | `configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported }` |
| `ReactTo<TEvent, THandler>` derives its processor id when `processorId` is omitted | Ids change from whatever was stored to the handler's derived type name | Keep the old id with `[ProcessorId("...")]` on the handler class, or carry the checkpoint position with `alberto ops checkpoint rename` |
| `ICheckpointStore` gains an optional `ICheckpointInventory` sibling | Nothing — it is a separate interface | Implement it on a custom store to opt into orphan detection |
| Migrations run at startup via `IHostedService`, not inside `AddAlberto` | Code that built an `IServiceProvider` and expected the schema to already exist | Start the host, or call `PostgresMigrator.Migrate(...)` directly in your own startup code |
| `TenancyOrderingValidator` deleted | Nothing — supersedes DX-6 from the audit cycle below | `.WithTenancy()` may now appear anywhere in the chain, in any order relative to `.WithPostgres()` |
| `AddAlbertoInstrumentation()` retained for manual `TracerProvider` / `MeterProvider` wiring | Nothing — the method is intentionally **not** marked `[Obsolete]` | Call it from `WithTracing(...)` or `WithMetrics(...)` when you wire OpenTelemetry without the hosting integration; calling it alongside `.WithTelemetry()` is also safe (`AddSource`/`AddMeter` are idempotent) |
| Unknown keys under `Alberto:Modules:{key}` now fail startup with `ALB0008` | A deployment with a misspelled or stale configuration key that was previously silently ignored **now fails at startup** with a did-you-mean suggestion | Correct or remove the key; the error message quotes the full path and, when close enough, suggests the correct spelling |
| `.WithTelemetry()` now installs the OpenTelemetry SDK unconditionally | Nothing functionally | Nothing required. With no exporters configured there is no I/O. The registration is observable in the container — called out here rather than hidden. |
| `Checkpoints:OrphanPolicy` defaults to `Strict` outside `Development` | A deployment whose handler was renamed at any point silently replaying events now **fails at startup** | This is the intended safety net. Either carry the position with `alberto ops checkpoint rename`, pin the old id with `[ProcessorId("...")]`, or set `Alberto:Modules:{key}:Checkpoints:OrphanPolicy` explicitly in configuration. See the note below. |
| `PostgresStateStore` positional constructor argument order fixed | Code that copied `(dataSource, tenantId, projectionType, schema)` from the old samples | Switch to named arguments: `new PostgresStateStore<T>(dataSource, projectionType, schema)` |

### Opting out of `Strict`

`Checkpoints` is configured through `Alberto:Modules:{key}:Checkpoints:*` only — unlike
`Postgres`, `ControlLoop` and `Telemetry`, it has no `With...()` builder method. To keep
`Warn` in a production environment, set the key explicitly:

```json
{
  "Alberto": {
    "Modules": {
      "orders": {
        "Checkpoints": { "OrphanPolicy": "Warn" }
      }
    }
  }
}
```

An explicitly configured value is honoured in every environment and is never escalated.

---

### Before / after: the Orders module

**Before (0.x — `Action<TOptions>` mutation style)**

```csharp
services.AddAlberto("orders", module => module
    .WithTenancy()
    .WithPostgres(o =>
    {
        o.ConnectionString = connectionString;
        o.AutoMigrate = false;
        o.Schema = "orders";
        o.MaxPoolSize = 30;
    })
    .WithTelemetry()
    .AddProjection(/* ... */)
    .WithControlLoop(o =>
    {
        o.PollingInterval = TimeSpan.FromMilliseconds(100);
        o.BatchSize = 500;
    }));
```

**After (1.0 — `Func<TOptions, TOptions>` with-expression style)**

```csharp
services.AddAlberto("orders", module => module
    .WithTenancy()
    .WithPostgres(o => o with
    {
        ConnectionString = connectionString,
        AutoMigrate = false,
        Schema = "orders",
        MaxPoolSize = 30,
    })
    .WithTelemetry()
    .AddProjection(/* ... */)
    .WithControlLoop(o => o with
    {
        PollingInterval = TimeSpan.FromMilliseconds(100),
        BatchSize = 500,
    }));
```

The full working 1.0 example is
`apps/Alberto.Orders/Alberto.Orders/Platform/OrdersModule.cs`.

---

## Summary — 2026-07-24 audit cycle

Fifteen breaking changes were introduced. They fall into the areas below:

| Finding | Area | Severity | What broke |
|---------|------|----------|------------|
| Architecture review | Event-store module | High | Backend-specific event-store types replaced by `EventStore` |
| Architecture review | Projection state | High | Unreachable transaction/list members removed from projection interfaces |
| Architecture review | Dependency lifetimes | High | `AlbertoStore` is scoped; outbox mappings get one scope per event |
| DX-15 / P3.1 | Event-store interface | High | `IEventStoreBackend` method renames + `IEventStoreHeadBackend` split |
| DX-10 | Event-store interface | Medium | `Register*` methods removed from `IEventStore` |
| DX-2 / DX-3 / DX-12 | Command/result API | Medium | `DecisionResult<TEvent>` obsoleted; `DecideAndAppendAsync` moved; `AddAlbertoStore` chained from builder |
| DX-8 | Consumer pipeline | Medium | `ReactTo` arity-ladder overloads removed |
| DX-5 | Packaging | Medium | `PostgresOutboxStore` moved to `Alberto.Dcb.Postgres.Messaging` |
| DX-6 | Tenancy | Low | `.WithTenancy()` after `.WithPostgres()` now fails loudly at startup |
| P1.1 | Tenancy | Low | Schema name restricted to lowercase identifier pattern |
| P1.3 | Tenancy | Low | `TenantEventStoreDecorator.StreamAll` now throws |
| P1.4 | Tenancy | Low | Startup tenancy-mode consistency check added |
| P1.5 | Tenancy | Low | `TenantContext.SetTenant` now validates tenant ID format |
| P0.7 | Consumer pipeline | Low | Inline-projection retry exhaustion wraps exception |
| DX-11 | Event-store interface | Low | `[Tag]` no longer valid on bare primary-constructor parameters |

---

## Architecture deepening

### Backend-specific event-store types replaced by `EventStore`

`PostgresEventStore` and `InMemoryEventStore` contained the same append, synchronous-projection,
and post-append orchestration. That behavior now lives once in `Alberto.Dcb.EventStore`; storage
variation remains behind the existing `IEventStoreBackend` seam.

```csharp
// Before
var store = new InMemoryEventStore(new InMemoryEventStoreBackend());
var postgresStore = new PostgresEventStore(postgresBackend);

// After
var store = new EventStore(new InMemoryEventStoreBackend());
var postgresStore = new EventStore(postgresBackend);
```

`EventStore` still implements both `IEventStore` and `IEventStoreConfigurator`.

### Projection state interface narrowed

`IStateStore<TState>.LoadManyAsync` and `ApplyChangesAsync` no longer accept an
`IDbTransaction`. No reachable event-store path supplied one: synchronous projections run after
the event append commits, and every built-in caller passed `null`. Each state-store adapter now
owns the transaction needed to apply its changes atomically.

`IStateStore<TState>.ListRecentAsync` was also removed. Projection persistence never called it;
inspection belongs on a query/admin surface instead of forcing every persistence adapter and
test fake to implement it.

`IInlineProjection.ProcessAsync` consequently no longer accepts an `IDbTransaction`.

```csharp
// Before
await stateStore.LoadManyAsync(ids, transaction, ct);
await stateStore.ApplyChangesAsync(upserts, deletes, transaction, ct);
await projection.ProcessAsync(events, transaction, ct);

// After
await stateStore.LoadManyAsync(ids, ct);
await stateStore.ApplyChangesAsync(upserts, deletes, ct);
await projection.ProcessAsync(events, ct);
```

### Scoped command and outbox dependencies

`AddAlbertoStore` now registers `AlbertoStore` as scoped. This matches the scoped event-store
adapter used by multi-tenant Postgres modules and prevents a singleton command store from
capturing the first tenant context.

Outbox mappings now receive a fresh dependency scope per event. Scoped mapper dependencies are
disposed after mapping, including when mapping fails; concurrent batch mappings never share a
scoped dependency.

---

## Consumer Pipeline

### DX-8 — `ReactTo` arity-ladder overloads removed

Six `ReactTo` overloads that accepted statically-typed dependency parameters (`TDep`,
`TDep1`/`TDep2`, `TDep1`/`TDep2`/`TDep3`) in both context-less and context-aware variants
have been deleted from `DcbModuleBuilderExtensions`.

**Why:** the arity ladder added cognitive overhead without adding power — every call site was a
thin wrapper around the factory form. The factory form already handles any number of
dependencies via `sp.GetRequiredService<T>()` with full IntelliSense.

The two supported shapes are now:

| Shape | Signature |
|---|---|
| **Factory form** | `ReactTo<TEvent>(Func<IServiceProvider, Func<TEvent, CancellationToken, Task>>, ...)` |
| **Factory form with context** | `ReactTo<TEvent>(Func<IServiceProvider, Func<TEvent, ReactorContext, CancellationToken, Task>>, ...)` |
| **Handler-class form** | `ReactTo<TEvent, THandler>(Func<THandler, Func<TEvent, CancellationToken, Task>>, ...)` |
| **Handler-class form with context** | `ReactTo<TEvent, THandler>(Func<THandler, Func<TEvent, ReactorContext, CancellationToken, Task>>, ...)` |

**Removed overloads:**
- `ReactTo<TEvent, TDep>(Func<TDep, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep>(Func<TDep, TEvent, ReactorContext, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2>(Func<TDep1, TDep2, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2>(Func<TDep1, TDep2, TEvent, ReactorContext, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2, TDep3>(Func<TDep1, TDep2, TDep3, TEvent, CancellationToken, Task>, ...)`
- `ReactTo<TEvent, TDep1, TDep2, TDep3>(Func<TDep1, TDep2, TDep3, TEvent, ReactorContext, CancellationToken, Task>, ...)`

The handler-class form `ReactTo<TEvent, THandler>(Func<THandler, Func<TEvent, ...>>)` is **not affected**.

**Migration — single dependency, no context:**

```csharp
// Before
builder.ReactTo<OrderPlaced, EmailService>(
    (svc, e, ct) => svc.SendConfirmationAsync(e.OrderId, ct),
    "order-email-reactor");

// After
builder.ReactTo<OrderPlaced>(
    sp =>
    {
        var svc = sp.GetRequiredService<EmailService>();
        return (e, ct) => svc.SendConfirmationAsync(e.OrderId, ct);
    },
    "order-email-reactor");
```

**Migration — single dependency, with `ReactorContext`:**

```csharp
// Before
builder.ReactTo<OrderPlaced, AuditLog>(
    (log, e, context, ct) => log.RecordAsync(e.OrderId, context.Timestamp, ct),
    "order-audit-reactor");

// After
builder.ReactTo<OrderPlaced>(
    sp =>
    {
        var log = sp.GetRequiredService<AuditLog>();
        return (e, context, ct) => log.RecordAsync(e.OrderId, context.Timestamp, ct);
    },
    "order-audit-reactor");
```

**Migration — two or more dependencies:** follow the same pattern, resolving each service from
`sp`. The factory form scales to any number of dependencies without needing a new overload.

**Handler-class form is unchanged:**

```csharp
// Unchanged — this is the handler-class form, not the arity ladder
builder.ReactTo<OrderPlaced, OrderReactor>(h => h.HandleAsync, "order-reactor");
```

---

### P0.7 — Inline EF projection retry exhaustion now throws `InlineProjectionExhaustedException`

**What changed:** `DeclaredEfInlineProjection` and `EfInlineProjection` retry a failed commit
up to 5 times on concurrency conflict. Previously exhaustion let the original exception propagate
with no distinguishing signal. After this change, on exhaustion a `Critical`-level log entry is
emitted and the exception is wrapped in `InlineProjectionExhaustedException`, which carries
`ProcessorId`, `Attempts`, and `DocumentCount` for structured alerting. The original exception
is available via `InnerException`.

**Why:** when exhaustion occurs, events are already durable in the event store but the inline
projection view is diverged. Without an explicit signal, operators have no way to know that a
replay is required; the divergence is silent until a consumer reads stale data.

**Impact:** breaking for callers that catch `DbUpdateConcurrencyException` on the inline-projection
path.

**Migration:**

```csharp
// Before
try
{
    await eventStore.AppendAsync(...);
}
catch (DbUpdateConcurrencyException ex)
{
    // handled inline-projection failure
}

// After
try
{
    await eventStore.AppendAsync(...);
}
catch (InlineProjectionExhaustedException ex)
{
    // All 5 retries exhausted: ex.ProcessorId, ex.Attempts, ex.DocumentCount available.
    // Schedule an async replay for the affected projection.
    logger.LogCritical("Projection {Id} diverged — replay required", ex.ProcessorId);
}
```

The exception type is in `Alberto.Dcb.EntityFramework.Inline.InlineProjectionExhaustedException`.

---

## Tenancy

### DX-6 — `.WithTenancy()` after `.WithPostgres()` now fails loudly at startup

**What changed:** a `TenancyOrderingValidator` hosted service is registered by `WithPostgres()`.
At application startup it checks whether `DcbModuleBuilder.HasTenancy` changed after
`WithPostgres()` was called. If it did, startup throws `InvalidOperationException`.

**Why:** previously, calling `.WithPostgres()` before `.WithTenancy()` silently registered a
single-tenant backend and ignored the tenancy flag — no error, no warning, just wrong behaviour.
The trap is trivially hit when a builder chain is reorganised.

**Migration — reorder the fluent chain:**

```csharp
// Before (wrong order — silently single-tenant)
builder.Services.AddAlberto("orders", module =>
    module
        .WithPostgres(o => o.ConnectionString = "...")
        .WithTenancy());

// After (correct order — .WithTenancy() before .WithPostgres())
builder.Services.AddAlberto("orders", module =>
    module
        .WithTenancy()
        .WithPostgres(o => o.ConnectionString = "..."));
```

---

### P1.1 — Schema name restricted to lowercase identifier; DDL now uses quoted identifier (SQL injection fix)

**What changed:** every PostgreSQL adapter, the admin/CLI surface,
`PostgresMigrator.Migrate()`, and `PostgresMigrator.GetPendingMigrations()` now validate the
`schema` parameter against the allowlist `^[a-z][a-z0-9_]{0,62}$`. Names that do not match
throw `ArgumentException` (or fail startup validation with `ALB1005`). Runtime SQL and the
internal `EnsureSchemaExists()` DDL use a double-quoted schema identifier.

**Why:** the schema name was previously validated only on the migration path. With
`AutoMigrate = false`, or when supplied through the CLI's `--schema` option, it was still
interpolated into runtime/admin SQL. A crafted schema name could execute arbitrary SQL with
the operator's database credentials. Severity: **critical**.

**Impact — breaking for schema names outside the allowlist:**

| Schema name | Before | After |
|-------------|--------|-------|
| `"orders"`, `"my_schema"`, `"public"` | Accepted | Accepted (no change) |
| `"MySchema"` (uppercase) | Accepted | `ArgumentException` |
| `"orders-v2"` (hyphen) | Accepted | `ArgumentException` |

**Migration:** lowercase your schema name and replace hyphens with underscores. If the schema
already exists in PostgreSQL with a non-conforming name, rename it first:

```sql
ALTER SCHEMA "MySchema" RENAME TO myschema;
```

---

### P1.3 — `TenantEventStoreDecorator.StreamAll` now throws

**What changed:** `TenantEventStoreDecorator.StreamAll()` (the request-scoped event store in
multi-tenant mode) now throws `InvalidOperationException` instead of silently forwarding to
`StreamAllTenants`.

**Why:** the previous behaviour was a silent data-isolation violation — any request-scoped code
that called `eventStore.StreamAll()` received events for all tenants, not just the active one.

**Impact:** breaking only in multi-tenant mode (`.WithTenancy()` active). Single-tenant
deployments are unaffected.

**Migration — option A (background loops using the consumer-feed backend):**

Background services such as `ControlLoop` and `DeadLetterRetryLoop` already use the
`":consumer"`-keyed backend correctly. Register your own background work against the same key:

```csharp
var backend = sp.GetRequiredKeyedService<IEventStoreBackend>(moduleKey + ":consumer");
await backend.StreamAll(afterPosition: lastPosition, ct: ct);
```

**Migration — option B (request handlers needing the active tenant's history):**

```csharp
// Returns events for the current tenant only — correct in a request-scoped context
await eventStore.StreamAsync(DcbQuery.Any(), afterPosition: 0, ct: ct);
```

A formal interface split (finding P3.1) that makes this distinction compile-time safe is
planned for a future breaking-changes window.

---

### P1.4 — Startup tenancy-mode consistency check

**What changed:** `WithPostgres()` now calls `PostgresMigrator.ValidateTenancyMode()` at
startup. It queries `information_schema.columns` to check whether the `tenant_id` column
exists in `alberto_events` and compares that against the configured tenancy mode. A mismatch
fails startup with a clear `InvalidOperationException`.

**Why:** running the wrong migration set against an existing database silently
`CREATE OR REPLACE`s stored functions with the wrong signatures, leading to cryptic failures
later. This check catches the mismatch before any harm is done.

**Impact:** may cause startup failures for existing deployments that have a tenancy mismatch.

**Recovery:** if you see the mismatch error, choose one of:
1. Change the application configuration to match the database (remove `.WithTenancy()` if the
   database is single-tenant).
2. Drop the schema and re-run the correct migration set.
3. Manually apply the missing migration scripts for the intended mode.

The check also runs when `AutoMigrate` is false — migrations must be fully applied before the
application starts.

---

### P1.5 — `TenantContext.SetTenant` now validates tenant ID format

**What changed:** `TenantContext.SetTenant(string tenantId)` now enforces the allowlist
`^[a-z][a-z0-9_]{0,62}$`. Calls with a non-matching value throw `ArgumentException`.

A tenant ID must start with a lowercase ASCII letter, contain only lowercase letters, digits,
and underscores, and be at most 63 characters long.

**Why:** only the sample HTTP interceptor previously applied a format check. Validating in
core ensures consistent rejection regardless of how tenant IDs reach the library.

**Impact — breaking for callers using tenant IDs outside the pattern:**

| Format | Matches? | Migration |
|--------|----------|-----------|
| `"acme"`, `"tenant1"`, `"us_east"` | Yes | No change required |
| `"Acme"` (uppercase) | No | Lowercase before calling `SetTenant` |
| `"acme-corp"` (hyphen) | No | Replace hyphens with underscores: `"acme_corp"` |
| `"550e8400-e29b-41d4-a716-446655440000"` (UUID) | No | Derive a slug, e.g. `"t_550e8400"` |
| `"TENANT"` (uppercase) | No | Lowercase |

**Before:**

```csharp
// Accepted any non-whitespace string
tenantContext.SetTenant("my-Tenant-UUID-550e8400");
```

**After:**

```csharp
// Must match ^[a-z][a-z0-9_]{0,62}$ or throws ArgumentException
tenantContext.SetTenant("my_tenant_id");   // OK
tenantContext.SetTenant("my-Tenant-UUID"); // throws ArgumentException
```

**Migration steps:**
1. Audit existing tenant IDs in the database (`SELECT DISTINCT tenant_id FROM alberto_events`).
2. For IDs that do not match, decide on a normalised slug and update all `SetTenant` call sites.
3. If your IDs cannot be changed (e.g. external UUIDs), adjust the validation regex in
   `TenantContext` and document the decision in an ADR.

---

## Event-Store Interface

### DX-15 / P3.1 — `IEventStoreBackend` method renames and `IEventStoreHeadBackend` split

This is the highest-impact interface change in this cycle. Only integrators that depend on
`IEventStoreBackend` directly are affected; usages through `IEventStore` (the high-level
public API) are unaffected.

#### DX-15 — Consistent `Async` suffix on `IEventStoreBackend`

The four methods that previously lacked the `Async` suffix were renamed:

| Before | After |
|--------|-------|
| `Stream(...)` | `StreamAsync(...)` |
| `StreamAll(...)` | `StreamAllAsync(...)` |
| `Append(...)` | `AppendAsync(...)` |
| `GetLastPosition(...)` | `GetLastPositionAsync(...)` |

`GetPositionsAsync` and `GetStableHeadAsync` already had the suffix and were not renamed (they
moved to `IEventStoreHeadBackend` — see below).

**Why:** `IEventStore` (the high-level public API) consistently uses the `Async` suffix.
The inconsistency made it easy to confuse the two interfaces and caused async-lint tools to
flag four methods per implementation.

#### P3.1 — `IEventStoreHeadBackend` interface extracted

`GetPositionsAsync` and `GetStableHeadAsync` were removed from `IEventStoreBackend` and placed
on a new dedicated interface:

```csharp
public interface IEventStoreHeadBackend
{
    Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default);

    Task<long> GetStableHeadAsync(
        long afterPosition, CancellationToken cancellationToken = default)
        => Task.FromResult(long.MaxValue);   // default: no barrier
}
```

`IEventStoreBackend` now has exactly four methods. All built-in backends
(`InMemoryEventStoreBackend`, `PostgresEventStoreBackend`, `TenantEventStoreDecorator`,
`InterceptingEventStoreBackend`) implement **both** interfaces. `EventStoreHead` now accepts
`IEventStoreHeadBackend` instead of `IEventStoreBackend`.

**Why:** `GetPositionsAsync` and `GetStableHeadAsync` are only ever called by `EventStoreHead`.
Placing them on `IEventStoreBackend` forced every implementer — including simple test fakes —
to provide two methods it never uses.

**Migration — custom `IEventStoreBackend` implementations:**

```csharp
// Before
public class MyBackend : IEventStoreBackend
{
    public Task<IReadOnlyCollection<IEventEnvelope>> Stream(...) { ... }
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(...) { ... }
    public Task<IReadOnlyCollection<IEventEnvelope>> Append(...) { ... }
    public Task<long> GetLastPosition(...) { ... }
    public Task<IReadOnlyList<long>> GetPositionsAsync(...) { ... }
    public Task<long> GetStableHeadAsync(...) { ... }
}

// After — rename methods + add IEventStoreHeadBackend
public class MyBackend : IEventStoreBackend, IEventStoreHeadBackend
{
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(...) { ... }
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(...) { ... }
    public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(...) { ... }
    public Task<long> GetLastPositionAsync(...) { ... }
    // These two now satisfy IEventStoreHeadBackend:
    public Task<IReadOnlyList<long>> GetPositionsAsync(...) { ... }
    // GetStableHeadAsync is optional — the interface default returns long.MaxValue.
}
```

**Migration — direct call sites on an `IEventStoreBackend` reference:**

```csharp
// Before
var events = await backend.Stream(query, ct: ct);
var all    = await backend.StreamAll(ct: ct);
var result = await backend.Append(events, query, expectedPos, ct);
var pos    = await backend.GetLastPosition(ct);

// After
var events = await backend.StreamAsync(query, cancellationToken: ct);
var all    = await backend.StreamAllAsync(cancellationToken: ct);
var result = await backend.AppendAsync(events, query, expectedPos, ct);
var pos    = await backend.GetLastPositionAsync(ct);
```

**Migration — calls to `GetPositionsAsync` / `GetStableHeadAsync` via an `IEventStoreBackend` reference:**

```csharp
// Before
var positions = await backend.GetPositionsAsync(after, windowSize, ct);

// After — cast to IEventStoreHeadBackend (safe for all built-in backends)
var positions = await ((IEventStoreHeadBackend)backend).GetPositionsAsync(after, windowSize, ct);
```

**Migration — test fakes for `EventStoreHead`:**

```csharp
// Before — had to stub the full IEventStoreBackend surface
private sealed class FakeBackend : IEventStoreBackend
{
    public Task<IReadOnlyList<long>> GetPositionsAsync(...) { ... }
    public Task<long> GetStableHeadAsync(...) { ... }
    // Unused stubs required by IEventStoreBackend:
    public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(...) => ...;
    // ...
}

// After — implement only the two methods EventStoreHead actually uses
private sealed class FakeBackend : IEventStoreHeadBackend
{
    public Task<IReadOnlyList<long>> GetPositionsAsync(...) { ... }
    public Task<long> GetStableHeadAsync(...) { ... }
}
```

**Migration — custom orchestration constructing `EventStoreHead` directly:**

```csharp
var backend     = services.GetRequiredService<IEventStoreBackend>();
var headBackend = backend as IEventStoreHeadBackend
    ?? throw new InvalidOperationException("Backend must implement IEventStoreHeadBackend");
var head = new EventStoreHead(headBackend, refreshInterval);
```

`ControlLoopBuilder` already does this cast internally; only code that constructs `EventStoreHead`
directly is affected.

---

### DX-10 — `Register*` methods removed from `IEventStore`; use `IEventStoreConfigurator`

> **Partly superseded.** `RegisterInlineProjection<TState, TProjection>` and
> `RegisterEfInlineProjection` have since been **removed** rather than moved. See
> *Deprecated projection and decision APIs removed* at the top of this file. The move of
> `RegisterInlineProjection(IInlineProjection)` and `RegisterPostAppendHandler` still applies.

**What changed:** three setup-time methods have been removed from `IEventStore`:
- `RegisterInlineProjection<TState, TProjection>(IStateStore<TState>)`
- `RegisterInlineProjection(IInlineProjection)`
- `RegisterPostAppendHandler(IPostAppendHandler)`

They now live on a new `IEventStoreConfigurator` interface (in `Alberto.Dcb`).
`EventStore` implements **both** `IEventStore` and `IEventStoreConfigurator`.
`RegisterEfInlineProjection` extension methods in
`Alberto.Dcb.EntityFramework` now extend `IEventStoreConfigurator` rather than `IEventStore`.

**Why:** `IEventStore` is the runtime consumer surface. Exposing setup-only methods on it lets
runtime code accidentally register projections or handlers after the store has already started
serving requests, leading to unpredictable ordering or missed events.

**Impact:** breaking for code that calls `Register*` through a variable typed as `IEventStore`,
or that implements `IEventStore` in a custom class with those methods.

**Migration — calling `Register*` through `IEventStore`:**

```csharp
// Before
IEventStore store = ...;
store.RegisterInlineProjection(myProjection);
store.RegisterPostAppendHandler(myHandler);

// After — option A (cast in builder/factory code where the concrete type is known)
if (store is IEventStoreConfigurator configurator)
{
    configurator.RegisterInlineProjection(myProjection);
    configurator.RegisterPostAppendHandler(myHandler);
}

// After — option B (resolve IEventStoreConfigurator directly)
IEventStoreConfigurator configurator = new EventStore(backend);
configurator.RegisterInlineProjection(myProjection);
```

**Migration — `RegisterEfInlineProjection`:**

```csharp
// Before
IEventStore store = ...;
store.RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(serviceProvider);

// After — cast to IEventStoreConfigurator (safe for built-in stores)
IEventStoreConfigurator configurator = (IEventStoreConfigurator)store;
configurator.RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(serviceProvider);
```

**Migration — custom `IEventStore` implementations:**

`Register*` methods are no longer required by `IEventStore`. To keep supporting setup-time
registration, implement `IEventStoreConfigurator` explicitly:

```csharp
public class MyCustomEventStore : IEventStore, IEventStoreConfigurator
{
    // IEventStore members (AppendAsync, StreamAsync, StreamAllAsync, GetLastPositionAsync)

    // IEventStoreConfigurator members
    public void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore) ...
    public void RegisterInlineProjection(IInlineProjection projection) ...
    public void RegisterPostAppendHandler(IPostAppendHandler handler) ...
}
```

---

### DX-11 — `[Tag]` no longer valid on bare primary-constructor parameters

**What changed:** `AttributeTargets.Parameter` has been removed from `TagAttribute`'s
`AttributeUsage`. Only `AttributeTargets.Property` is now valid.

**Why:** tag extraction reads properties via reflection (`GetProperties()`). Applying
`[Tag(...)]` to a primary-constructor parameter without the `[property:]` specifier placed
the attribute on the parameter, not the synthesised property — so no tags were ever extracted,
silently, at runtime. Restricting the target to `Property` turns this into a compile-time error.

**Migration:** add the `property:` specifier:

```csharp
// Before (compiled but produced no tags — silent bug):
public record OrderPlaced(
    [Tag("order")] Guid OrderId) : IEvent;

// After (attribute lands on the synthesised property — correct behaviour):
public record OrderPlaced(
    [property: Tag("order")] Guid OrderId) : IEvent;
```

The `[property: Tag(...)]` form was already shown in the XML-doc example and has always
produced the correct behaviour; only the bare `[Tag(...)]` on parameters is now rejected.

---

## Command/Result API

### DX-2 / DX-3 / DX-12 — `DecisionResult<TEvent>` obsoleted; `DecideAndAppendAsync` moved; `AddAlbertoStore` chained from builder

**Why:** the library shipped three overlapping result types and two-and-a-half separate command
paths. `DecisionResult<TEvent>` (abstract record, pattern-matched, string-based failure)
overlapped with `Decision` / `Decision<T>` (struct, `IsSuccess`/`IsError`, carries `Problem`
list) and `Result` / `Result<T>` (pipeline output). Having three types for the same concept
confused integrators about which to use. `Decision` / `Decision<T>` are the correct types for
the "should we append?" question; `Result` / `Result<T>` model the outcome of the persist step.
`DecisionResult<TEvent>` had overlap with both and is now redundant. Additionally,
`DecideAndAppendAsync` on `IEventStoreBackend` was invisible to consumers that inject `IEventStore`,
and the standalone `AddAlbertoStore` call was disconnected from the `AddAlberto` builder.

#### 1. Single blessed decide-result type: `Decision` / `Decision<T>`

> **Superseded.** `DecisionResult<TEvent>` has since been **removed**. See *Deprecated projection
> and decision APIs removed* at the top of this file.

`DecisionResult<TEvent>` is now `[Obsolete]` and will be removed in a future version.

| Before | After |
|---|---|
| `DecisionResult<TEvent>.Success(evt)` | `Decision.Succeed(evt)` |
| `DecisionResult<TEvent>.Failure("reason")` | `Decision.Fail(Problem.Create("code", "reason"))` |
| `result is DecisionResult<TEvent>.Ok ok` | `result.IsSuccess` / `result.Events` |
| `result is DecisionResult<TEvent>.Fail fail` | `result.IsError` / `result.Problems` |

**Migration:**

```csharp
// Before
public static DecisionResult<IEvent> Create(...)
{
    if (alreadyExists) return DecisionResult<IEvent>.Failure("Order already exists");
    return DecisionResult<IEvent>.Success(new OrderCreated(...));
}

// After
public static Decision Create(...)
{
    if (alreadyExists) return Decision.Fail(Problem.Create("already-exists", "Order already exists"));
    return Decision.Succeed(new OrderCreated(...));
}
```

#### 2. `DecideAndAppendAsync` moved from `IEventStoreBackend` to `IEventStore`

The extension method now lives in `Alberto.Dcb.Commands` and extends `IEventStore`. Signature
changes:

| Aspect | Before | After |
|---|---|---|
| Host interface | `IEventStoreBackend` | `IEventStore` |
| Decision function | `Func<TState, DecisionResult<TEvent>>` | `Func<TState, Decision>` |
| Event mapper | `Func<TEvent, IEventToPersist>` | `Func<IEvent, IEventToPersist>` |
| Return type | `Task<DecisionResult<TEvent>>` | `Task<Result>` |
| Type parameters | `<TState, TEvent>` | `<TState>` |

**Migration:**

```csharp
// Before  (on IEventStoreBackend)
var result = await backend.DecideAndAppendAsync<OrderState, IEvent>(
    boundary,
    evolver,
    state => state.Exists
        ? DecisionResult<IEvent>.Failure("Already exists")
        : DecisionResult<IEvent>.Success(new OrderCreated(...)),
    @event => new EventToPersist { ... },
    ct);

// After  (on IEventStore)
var result = await eventStore.DecideAndAppendAsync<OrderState>(
    boundary,
    evolver,
    state => state.Exists
        ? Decision.Fail(Problem.Create("already-exists", "Already exists"))
        : Decision.Succeed(new OrderCreated(...)),
    @event => new EventToPersist { ... },
    ct);
```

#### 3. `AddAlbertoStore` chains from the builder

> **Superseded.** `AddAlbertoStore` has since been removed entirely. If you are migrating from
> before this cycle, skip straight to `WithEventsFrom` — see the section at the top of this file.
> The rest of this block is kept as a record of what this cycle changed.

The standalone overload `services.AddAlbertoStore(moduleKey, assembly)` is now `[Obsolete]`.

**Migration:**

```csharp
// Before  (standalone, disconnected from builder)
services.AddAlberto("orders", builder => builder.WithPostgres(...));
services.AddAlbertoStore("orders", typeof(OrderCreated).Assembly);

// After  (chained from builder)
services.AddAlberto("orders", builder => builder
    .WithPostgres(...)
    .AddAlbertoStore(typeof(OrderCreated).Assembly)
);
```

The `AlbertoStore.Handle(...).Validate(...).Load(...).Decide(...).Persist(...)` fluent pipeline
is unchanged and remains the primary recommendation.

---

## Packaging

### DX-5 — `PostgresOutboxStore` moved to `Alberto.Dcb.Postgres.Messaging`

**What changed:** `PostgresOutboxStore` has been extracted from `Alberto.Dcb.Postgres` into a
new dedicated package: **`Alberto.Dcb.Postgres.Messaging`**. Its namespace changed from
`Alberto.Dcb.Postgres` to `Alberto.Dcb.Postgres.Messaging`.

**Why:** adding a reference to `Alberto.Dcb.Postgres` previously pulled in `Alberto.Dcb.Messaging`
transitively, forcing every Postgres user to take a dependency on the outbox/messaging stack
they might not need.

**Migration — if you use `PostgresOutboxStore`:**

1. Add the new package reference:

```xml
<!-- Before: came in transitively — no explicit reference needed -->

<!-- After: add the explicit reference -->
<PackageReference Include="Alberto.Dcb.Postgres.Messaging" Version="x.x.x" />
```

2. Update the `using` directive:

```csharp
// Before
using Alberto.Dcb.Postgres;

// After
using Alberto.Dcb.Postgres.Messaging;
```

The type name `PostgresOutboxStore` and its constructor signature are unchanged.

**Migration — if you do NOT use `PostgresOutboxStore`:** no action required. If you were
relying on the transitive `Alberto.Dcb.Messaging` reference for other messaging types, add
`Alberto.Dcb.Messaging` directly.

---

## Breaking Changes — Earlier Release

### 1. Admin package removed — use the CLI instead

`Alberto.Dcb.Admin` and the embedded Angular admin UI have been removed. Replace with the
`alberto` .NET global tool:

```bash
dotnet tool install -g Alberto.Cli
```

**Remove from your modules:**

```csharp
// Before
builder.AddAlbertoModule(module => module
    .WithPostgres(...)
    .WithAdmin(admin => { admin.Title = "Orders"; }));   // ← remove

// After
builder.AddAlbertoModule(module => module
    .WithPostgres(...));
```

**Remove from your app startup:**

```csharp
// Before
builder.Services.AddPostgresAdminSubscriptions();        // ← remove
app.MapDcbAdmin();                                       // ← remove
```

**Remove from your `.csproj`:**

```xml
<!-- remove this -->
<PackageReference Include="Alberto.Dcb.Admin" />
```

**CLI quick reference:**

```bash
alberto status                                  # system overview
alberto processor <id>                          # processor details
alberto checkpoints                             # all checkpoints
alberto dead-letters --processor <id>           # dead letters
alberto events --type <type> --limit 50         # event browser
alberto projections list <type>                 # projection states
alberto tenants                                 # tenant leases
alberto ops rebuild <id>                        # reset checkpoint → full replay
alberto ops checkpoint reset <id>               # reset checkpoint
alberto ops dead-letters retry-rewind <id>      # rewind to earliest dead letter
alberto ops tenants release                     # release all tenant leases
```

Connection defaults to `Host=localhost;Database=postgres`. Override via `--url`,
`ALBERTO_URL` env var, or `.alberto/config.json`.

---

### 2. Multi-tenant apps must opt in to tenancy

Single-tenant is now the default. If your app uses `X-Tenant-Id` header routing and per-tenant
event isolation, add `.WithTenancy()`:

```csharp
// Before (implicitly multi-tenant)
builder.AddAlbertoModule(module => module
    .WithPostgres(...));

// After (explicit opt-in)
builder.AddAlbertoModule(module => module
    .WithPostgres(...)
    .WithTenancy());
```

Single-tenant apps gain a simpler schema (no `tenant_id` column). Run
`PostgresMigrator.Migrate(connectionString, singleTenant: true)` to use the single-tenant
migration set.

---

### 3. New database migrations (run automatically on startup)

Five new migrations are applied automatically when the application starts:

| # | Name | What it adds |
|---|------|-------------|
| 013 | DeadLetterPosition | `global_position` column on dead letters |
| 014 | Outbox | `outbox_entries` table (if using `Alberto.Dcb.Messaging`) |
| 015 | TenantAssignments | `tenant_assignments` table for consistent hash ring |
| 016 | FencedCheckpoint | `save_checkpoint_if_lease_held` SQL function |

No manual steps required — `PostgresMigrator.Migrate()` handles them.

---

## ~~Deprecations (still work, emit compiler warnings)~~

**Nothing in this section still compiles.** Every entry below has since been removed; each links to
the section that records the removal. Kept for the migration paths, not as a statement of what the
current API accepts.

### ~~Old projection API → `DeclareProjection`~~

Removed — see *Deprecated projection and decision APIs removed* at the top of this file for the
current shape of `DeclareProjection` (which differs from the sketch below: the document-ID selector
is per-event on `.On<TEvent>(id:, apply:)`, not a single `.WithId(...)`, and `For<TState>` takes the
processor ID).

```csharp
// Before (removed)
public class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderPlaced>
{
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, IEventEnvelope<OrderPlaced> envelope) { ... }
}

consumer.AddProjection<OrderSummary, OrderSummaryProjection>(...);

// After
var declaration = DeclareProjection.For<OrderSummary>("order-summary")
    .On<OrderPlaced>(
        id: e => e.OrderId.ToString(),
        apply: (state, e, ctx) => state with { ... })
    .Build();

builder.AddProjection(declaration, ...);
```

### ~~Old filter API → middleware~~

Removed. `IConsumeFilter` and `AddFilter<T>` no longer exist.

```csharp
// Before (removed)
consumer.AddFilter<MyConsumeFilter>();

// After
consumer.WithMiddleware(ConsumeMiddlewares.RetryAndDeadLetter());
consumer.WithMiddleware(async (ctx, next) => { /* custom logic */ await next(); });
```

### ~~`DecisionResult<TEvent>` → `Decision` / `Decision<T>`~~

Removed. See the Command/Result API section above for the full migration.

### ~~Standalone `AddAlbertoStore(moduleKey, assembly)` → builder chaining~~

No longer a deprecation — both spellings of `AddAlbertoStore` have since been **removed**. See
`AddAlbertoStore` removed — use `WithEventsFrom` at the top of this file.
