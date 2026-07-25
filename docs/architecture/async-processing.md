# Async Processing Architecture

This document describes how events flow through the async processing pipeline in Alberto, from being written to the event store through the control loop to projection and reactor processing.

## High-Level Flow

```
Event appended to store
    ↓
PostgreSQL alberto_events table
    ↓
NOTIFY on {schema}_events refreshes EventStoreHead ahead of its timer
    ↓
ControlLoop reads a batch from the global stream
    ↓
Batch dispatched through the middleware chain
    ↓
Events routed to processors (AsyncProjection / AsyncReactor / BatchedEfProjection)
    ↓
State written via IStateStore (PostgresStateStore or EfStateStore)
    ↓
Checkpoint saved (CachingCheckpointStore → PostgresCheckpointStore)
```

## The control loop

`ControlLoop` (`src/Alberto.Dcb/Subscriptions/ControlLoop.cs`) is the central coordinator. One loop runs per module; `ControlLoopGroup` owns the set.

Each cycle:

1. Read the current head position from `EventStoreHead`, which refreshes on its own interval and is nudged early by `IEventAppendedSignal` when Postgres NOTIFY fires.
2. Read a batch of events after the processor's checkpoint.
3. Dispatch the batch through the middleware chain.
4. Save the checkpoint.
5. Back off to the polling interval if there was nothing to do.

### Middleware

`MiddlewareRunner` (`src/Alberto.Dcb/Subscriptions/MiddlewareRunner.cs`) builds two chains from the same generic core:

| Chain | Context | Middleware file |
|-------|---------|-----------------|
| Single-event | `ConsumeEventContext` | `ConsumeMiddleware.cs` |
| Batch | `BatchConsumeContext` | `BatchConsumeMiddleware.cs` |

Both contexts implement `IMiddlewareContext` (`ProcessorId`, `ModuleKey`, `Attempt`, `LastError`, `CancellationToken`). The retry-and-dead-letter behaviour that used to be duplicated across the two middlewares now lives once in `RetryAndDeadLetterCore.ExecuteAsync`, which drives the attempt loop and returns the final error (or `null` on success). The two middlewares differ only in what they do with that error:

- **Single event** — dead-letter it and advance.
- **Batch** — if the batch holds more than one event, rethrow so the caller can split the batch and isolate the poison event; a single-event batch is dead-lettered directly.

Negative `MaxRetries` is rejected at startup by validator `ALB0007`, which guarantees the attempt loop always runs at least once and always produces an error to act on.

### Error handling

```
Event processing fails
    ↓
ErrorClassifier.Classify(ex)
    ↓
Permanent ──────────────────────────────┐
    ↓                                   │
Transient: retry up to MaxRetries       │
with exponential backoff                │
(RetryDelay × BackoffMultiplier^n,      │
 capped at MaxRetryDelay)               │
    ↓                                   │
Attempts exhausted ─────────────────────┤
                                        ▼
                          DeadLetterOnMaxRetries?
                             ├─ yes → IDeadLetterStore.AddAsync, skip event
                             └─ no  → skip event
```

`DeadLetterRetryLoop` separately picks up dead letters that an operator has marked for retry (`IDeadLetterStore.MarkForRetryAsync`, exposed as `alberto ops dead-letters retry`).

## Key Components

### AsyncProjection

Transforms events into read-model state using pure functions.

**Location:** `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs`

1. Extract document ID from the event
2. Get the tenant-scoped state store
3. Load current state (or create new)
4. Apply the event → `Set`, `Delete`, or `Unchanged`
5. Persist the result

### AsyncReactor

Handles side effects in response to events.

**Location:** `src/Alberto.Dcb/Subscriptions/AsyncReactor.cs`

- Reflection-based dispatch to `IReact<TEvent>` handlers
- No state persistence
- Scans the reactor type for implemented interfaces at startup

### BatchedEfProjection

**Location:** `src/Alberto.Dcb.EntityFramework/Batching/BatchedEfProjection.cs`

Accumulates a batch of events in the EF change tracker and flushes with a single `SaveChanges`.

### Checkpoint stores

The Postgres backend wires `CachingCheckpointStore` over `PostgresCheckpointStore`:

| Store | Role |
|-------|------|
| `CachingCheckpointStore` | In-memory read/write cache; marks entries dirty and flushes on a timer |
| `PostgresCheckpointStore` | The durable store; also implements `IFencedCheckpointStore` |

`SaveAsync` is **monotonic** — the Postgres upsert uses `GREATEST`, so a processor can never move its own checkpoint backwards. `RewindAsync` is the deliberate escape hatch that writes unconditionally; it is intended only for operator-initiated rewinds and both decorators bypass their caches and write straight through.

`SaveIfLeaseHeldAsync` on `IFencedCheckpointStore` makes the write conditional on the caller still holding the processor or tenant lease, so a partitioned replica cannot overwrite a newer checkpoint.

### PostgresEventListener

**Location:** `src/Alberto.Dcb.Postgres/PostgresEventListener.cs`

LISTENs on the `{schema}_events` channel and raises `IEventAppendedSignal` so the control loop wakes immediately instead of waiting for the next poll. The trigger that emits the notification is in `010_BatchNotifyTrigger.sql` and fires once per append batch, not once per event.

## Configuration

For the canonical defaults reference see [configuration.md](../configuration.md#controlloop-options).
The table below shows how each knob is set in code and in configuration:

| Setting | Code | Default | Configuration key |
|---------|------|---------|-------------------|
| Polling interval | `.WithControlLoop(o => o with { PollingInterval = ... })` | 250 ms | `ControlLoop:PollingInterval` |
| Batch size | `.WithControlLoop(o => o with { BatchSize = ... })` | 100 | `ControlLoop:BatchSize` |
| Head refresh interval | `.WithControlLoop(o => o with { HeadRefreshInterval = ... })` | 100 ms | `ControlLoop:HeadRefreshInterval` |
| Max retries | `.WithControlLoop(o => o with { Retry = o.Retry with { MaxRetries = ... } })` | 3 | `ControlLoop:Retry:MaxRetries` |
| Retry delay | `.WithControlLoop(o => o with { Retry = o.Retry with { RetryDelay = ... } })` | 1 s, doubling to 30 s | `ControlLoop:Retry:RetryDelay` |
| Dead-letter on exhaustion | `.WithControlLoop(o => o with { Retry = o.Retry with { DeadLetterOnMaxRetries = ... } })` | `true` | `ControlLoop:Retry:DeadLetterOnMaxRetries` |

## Module Configuration Example

Taken from `apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs`:

```csharp
services.AddAlberto(ModuleKey, builder => builder
    .WithTenancy()
    .WithPostgres(o => o with
    {
        ConnectionString = connectionString,
        AutoMigrate = false,
        Schema = "orders",
        MaxPoolSize = 30,
    })
    .WithEntityFramework<OrdersDbContext>(options =>
    {
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
    })
    .WithTelemetry()
    .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
        return () => new PostgresStateStore<OrdersOverview>(
            dataSource,
            projectionType: nameof(OrdersOverviewProjection),
            schema: "orders",
            rebuildVersion: ctx.RebuildVersion);
    })
    .AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
    .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 }));
```

`RetryOptions` is an immutable record; use a `with` expression to override individual fields:

```csharp
.WithControlLoop(o => o with
{
    Retry = o.Retry with
    {
        MaxRetries = 5,
        RetryDelay = TimeSpan.FromSeconds(2),
    }
})
```

To supply a custom error classifier, call `UseErrorClassifier<T>()` on the module builder rather
than setting a property on the options record.

## Projection rebuilds

Changing how a projection interprets history means its stored state is wrong. A rebuild replays the whole log into a *second copy* of that projection's state while the live copy keeps serving reads, then swaps the two in one transaction. Readers move from a complete old projection to a complete new one; there is no window in which the projection is empty or half-built.

Every projection state row carries a `rebuild_version`. One version is *active* — the one readers and the live control loop use. While a rebuild runs, a second version exists that only the shadow loop can see.

```
              live loop ──────────────────────────────▶  version 1  ◀── readers
                                                              │
  start ──▶  shadow loop (own checkpoint, from position 0) ─▶ version 2
                                                              │
  promote ──▶ version 2 becomes active, version 1 deleted ────┘  (one transaction)
```

### Enabling it

```csharp
services.AddAlberto("orders", builder => builder
    .WithPostgres(...)
    .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
    {
        var dataSource = ctx.Services.GetRequiredKeyedService<NpgsqlDataSource>("orders");
        return () => new PostgresStateStore<OrdersOverview>(
            dataSource, nameof(OrdersOverviewProjection), "orders",
            rebuildVersion: ctx.RebuildVersion);   // <- the projection follows the version
    })
    .WithRebuilds());
```

Two requirements, and a projection that meets neither will have its live state overwritten by the replay instead of shadowed:

- **State stores must resolve their version through `ctx.RebuildVersion`.** It is a `Func<int>` rather than a value because promotion has to take effect underneath a store that is already running. Pass it straight through; do not call it once and cache the result.
- **EF projection entities must be configured with `ProjectionEntity<TEntity>()`** in `OnModelCreating`, which makes the key `(DocumentId, RebuildVersion)`. Without the version in the key the shadow rows collide with the live rows on insert.

`WithRebuilds()` registers the machinery but starts nothing. A rebuild only happens when an operator asks for one.

### Running one

```bash
alberto ops rebuild start OrdersOverviewProjection
alberto ops rebuild status
alberto ops rebuild abort OrdersOverviewProjection
```

The replay runs *in the application*, not in the CLI: the CLI only moves the state machine, and a module without `WithRebuilds()` will leave a started rebuild sitting at `rebuilding` forever. With `WithRebuilds(autoPromote: false)` a finished rebuild parks at `ready` until `alberto ops rebuild promote <processor>`.

### How it works

`RebuildCoordinator` is a hosted service that owns no state of its own — everything it does is derived from `alberto_projection_rebuild_meta`, so a rebuild started from the CLI in one process is picked up in another, and a coordinator that crashes mid-rebuild resumes on restart. Each tick it:

1. Refreshes `ProjectionVersions`, the module's single cached view of the state machine. Every version selector resolves from this cache, so the coordinator and the stores it configures always agree.
2. Starts a shadow control loop for each rebuild in flight — `ready` included, since a rebuild waiting on an operator still has to track the live loop, and a replica that restarted while one was already `ready` would otherwise have no loop to catch it up at all. The shadow loop uses its own checkpoint key (`<processor>::rebuild::<version>`) so replaying from position 0 does not drag the live projection back with it, and it always takes the batch path.
3. Marks a rebuild `ready` once its shadow checkpoint passes the target position captured at start. The shadow loop keeps running past that point, so events that arrive during the replay are in the rebuilt version too.
4. Promotes, but only once the shadow checkpoint has drawn level with the live processor's. It then stops the shadow loop, flips the version and deletes the superseded state rows in one transaction, moves the live checkpoint up to the position the shadow reached, and tells any `IProjectionStateClearer` about backends the transaction could not reach (EF projections, in particular).

Three properties do most of the safety work here, and all three are load-bearing rather than tidiness:

- **Promotion waits for the rebuilt version to catch up with the live one, and hands over its position.** Reaching the target only means the *historical* replay is done: the target was the head of the log when the rebuild started, and everything appended since has been applied by the live loop to the very version promotion is about to delete. Flip while the shadow is behind and the promoted copy is missing exactly those events, permanently — the live loop's checkpoint is already past them, so nothing goes back. The mirror image matters too: the shadow is usually *ahead* of the live loop, and a live loop left at its own checkpoint would apply everything in between a second time, into the version now serving reads. Fast-forwarding it to the shadow's position at promotion is what makes the hand-off exact.

- **Version numbers are never reused.** `alberto_projection_rebuild_meta.last_allocated_version` is a high-water mark, and a rebuild takes `last_allocated_version + 1`. Allocating `active_version + 1` instead would be correct after a promotion — which advances the active version — but not after an abort, which deliberately leaves it alone. Abort deletes the abandoned version's rows and flips the status in one transaction, but the shadow loop writing into that version lives in the application process and only learns of the abort on its next tick, so its late writes outlive the delete. Reallocating that number seeds the next replay with those leftovers and applies every event twice.
- **The shadow checkpoint belongs to the version, not to the processor.** A key per processor has to be reset between rebuilds, and every moment the coordinator could do that is a moment an operator can start the next rebuild ahead of. A rebuild begun in that window would resume from a checkpoint already at the head of the log, replay nothing, and promote an empty projection. A version-scoped key has no such window: a version nobody has replayed has no checkpoint, and one being resumed after a restart has exactly the position it left off at.

Because aborts consume numbers without moving the active one, the startup sweep runs from `1` to one past the highest version the processor knows about — the version being rebuilt if one is in flight, the active one otherwise — cleaning up after a promotion or abort that happened while the coordinator was down.

### Limits

- One rebuild per processor at a time. `StartAsync` is guarded against the state it is leaving, so two operators racing cannot both win.
- The shadow loop runs under the same lease-free assumption as the rest of the module. Enable leases (`.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })`) if more than one replica runs the module, or two replicas will replay into the same version.
- A rebuild reprocesses every event through the projection. Reactors are not rebuilt — replaying side effects is not something the coordinator can make safe.

## Not implemented

The following appear in the schema or the type system but have no orchestration behind them. Do not rely on them.

- **Rebuild-mode processor tuning.** `IEventProcessor.IsRebuilding` is set for shadow loops, but there is no lag threshold and no separate rebuild batch size — a shadow loop runs on the module's configured batch size.
- **Real-time admin push.** There is no admin HTTP API, no GraphQL admin subscriptions, and no admin dashboard in this repository. The `{schema}_events` NOTIFY channel exists to refresh `EventStoreHead`, not to feed a UI. The operator surface is the CLI in `tools/Alberto.Cli`.

## Key Files

| Component | File |
|-----------|------|
| ControlLoop | `src/Alberto.Dcb/Subscriptions/ControlLoop.cs` |
| ControlLoopGroup | `src/Alberto.Dcb/Subscriptions/ControlLoopGroup.cs` |
| MiddlewareRunner | `src/Alberto.Dcb/Subscriptions/MiddlewareRunner.cs` |
| Retry / dead-letter core | `src/Alberto.Dcb/Subscriptions/RetryAndDeadLetterCore.cs` |
| Single-event middleware | `src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs` |
| Batch middleware | `src/Alberto.Dcb/Subscriptions/BatchConsumeMiddleware.cs` |
| RetryOptions | `src/Alberto.Dcb/Configuration/RetryOptions.cs` |
| IErrorClassifier | `src/Alberto.Dcb/Subscriptions/IErrorClassifier.cs` |
| AsyncProjection | `src/Alberto.Dcb/Subscriptions/AsyncProjection.cs` |
| AsyncReactor | `src/Alberto.Dcb/Subscriptions/AsyncReactor.cs` |
| CachingCheckpointStore | `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs` |
| PostgresCheckpointStore | `src/Alberto.Dcb.Postgres/PostgresCheckpointStore.cs` |
| PostgresEventListener | `src/Alberto.Dcb.Postgres/PostgresEventListener.cs` |
| EfStateStore | `src/Alberto.Dcb.EntityFramework/EfStateStore.cs` |
| BatchedEfProjection | `src/Alberto.Dcb.EntityFramework/Batching/BatchedEfProjection.cs` |
| RebuildCoordinator | `src/Alberto.Dcb/Subscriptions/RebuildCoordinator.cs` |
| ProjectionVersions | `src/Alberto.Dcb/Subscriptions/ProjectionVersions.cs` |
| IProjectionRebuildStore | `src/Alberto.Dcb/Subscriptions/IProjectionRebuildStore.cs` |
| PostgresProjectionRebuildStore | `src/Alberto.Dcb.Postgres/PostgresProjectionRebuildStore.cs` |
| NOTIFY trigger | `src/Alberto.Dcb.Postgres/Migrations/010_BatchNotifyTrigger.sql` |
