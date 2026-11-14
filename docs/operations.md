# Operations

Running Alberto in production comes down to four questions:

1. **Is anything falling behind?** Checkpoint lag.
2. **Is anything stuck?** Dead letters.
3. **Who is doing the work?** Processor and tenant leases.
4. **How do I intervene?** The operator interfaces.

Alberto ships no operator tool and no admin API. Every intervention is a call on an interface
the module already registers, and every question is answered by the module's own tables or its
metrics. Where those calls are exposed (an endpoint behind your own authorization, a background
job, a one-off console app) is the application's decision, and so is recording who made them:
Alberto writes no audit trail for operator calls.

## The operator interfaces

Each module registers them in DI under its module key:

```csharp
var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>("orders");
var deadLetters = sp.GetRequiredKeyedService<IDeadLetterStore>("orders");
var rebuilds    = sp.GetRequiredKeyedService<IProjectionRebuildStore>("orders");
```

| Interface | Does |
|---|---|
| `ICheckpointStore` | `GetAsync`, `ResetAsync` (back to 0), `RewindAsync` (to an exact position) |
| `IDeadLetterStore` | `GetAsync`, `CountAsync`, `MarkForRetryAsync`, `ClearAsync` |
| `IProjectionRebuildStore` | `ListAsync`, `GetAsync`, `StartAsync`, `RequestPromotionAsync`, `RequestAbortAsync` |
| `ITenantShardMap` | `ResolveAsync`, `AssignAsync`, `GetAllAsync`: the tenant → shard catalog |

On a [sharded module](architecture/tenant-sharding.md) each shard is a complete module registered
under `ShardKey.Compose(moduleKey, shardId)`, so resolve the stores per shard. **Positions are per
database**: never apply one shard's position to another's checkpoint.

**`ITenantShardMap.AssignAsync` is permanent.** It is first-writer-wins: assigning a tenant that
already has a shard returns the shard it is in rather than moving it. There is no relocation:
moving a tenant between databases means migrating its events, checkpoints, projection state and
leases across two unrelated position sequences, which is a migration you write and verify against
your own data.

## Inspecting

The tables are the source of truth, and reading them still works when the application is the
thing that is broken. Qualify each with the module's schema (`orders` below):

```sql
-- The head, and every processor's position, lag and fault
SELECT c.processor_id, h.head - c.last_position AS lag, c.updated_at, c.faulted_at, c.fault_message
FROM orders.alberto_processor_checkpoints c,
     (SELECT MAX(global_position) AS head FROM orders.alberto_events) h
ORDER BY lag DESC;

-- Dead letters per processor; next_retry_at NULL means parked
SELECT processor_id, count(*), min(global_position), min(next_retry_at)
FROM orders.alberto_dead_letter_events
GROUP BY processor_id;

-- Tenant leases: who holds what, until when
SELECT tenant_id, consumer_id, replica_id, expires_at
FROM orders.alberto_tenant_leases
ORDER BY tenant_id;
```

**A module with a custom schema is queried in that schema.** Pointing the queries at `public` on a
store that lives in `orders` reports an empty, healthy-looking system.

### Reading lag

For alerting, use the `alberto.processor.lag` gauge (events a processor is behind, tagged
`processor`, `module`, `shard`) rather than polling SQL. A few hundred events of lag on a busy
module is normal: the control loop polls. Lag that only grows means the processor is failing,
blocked, or slower than the write rate; lag that is pinned at exactly the same number means it has
stopped entirely (check dead letters and whether the application is running at all). A
checkpoint whose `updated_at` has not moved in minutes on a live system is a stalled processor.

**A lag of zero does not always mean caught up.** Lag is computed against the *stable* head —
the position the in-flight visibility barrier (`EnableStableHeadBarrier`, Postgres only) allows
processors to see, not the log's true tail. If any write transaction is left open anywhere on
the same Postgres server — an idle-in-transaction session, a long batch, an orphaned prepared
transaction, even against a *different* database on that server — Postgres's `xmin` horizon
freezes, the barrier stops advancing, and every processor's lag reads a steady, unremarkable
number even though new events have committed and nothing is being delivered. Watch
`alberto.head.stalled` (below) for this case; lag alone cannot distinguish it from genuinely
being caught up.

On a sharded module lag is **per shard**: each database has its own `position` sequence, so a
head from `db1` against a checkpoint from `db2` is a meaningless number.

## Errors, retries and dead letters

Every async processor runs behind the retry-and-dead-letter middleware. One event's failure never
takes down the loop.

```
handler throws
    ↓  classify
transient?  ──yes──▶ retry with exponential backoff, up to MaxRetries
    │no
    ▼
permanent ──────────▶ dead letter immediately, no retries
```

Retries are exhausted → for a reactor, the event is written to `alberto_dead_letter_events` with
its error message, stack trace and attempt count, and **the checkpoint advances past it**. A poison
event does not block the ones behind it.

A **projection is different: it never dead-letters.** A read model with a skipped event is wrong
for good, so the projection stops at the failing event, retries it in place (1 s doubling to 1 min)
and carries on in order once it works. While it waits it is *halted*: `alberto.processor.halted`
is above zero, `alberto.processor.halts` counts it, the processor reports unhealthy, and an error
log names the processor and global position (never the payload). Fix the cause; no operator action
is needed to resume.

### Automatic redelivery

A reactor's dead letter with a transient (or unknown) error comes back by itself. Each repeat
failure updates the same row and waits longer; with the defaults the waits are 1 min, 4 min, 16 min,
about 1 h 4 min, about 4 h 16 min and then 12 h for the last five: ten redeliveries over about 2.7
days. After the last one the row stays parked (`next_retry_at` is `NULL`) until
`IDeadLetterStore.MarkForRetryAsync` is called for it. A permanent error parks at once.

```csharp
.WithControlLoop(o => o with
{
    Redelivery = o.Redelivery with
    {
        Enabled = true,                           // default
        MaxRedeliveries = 10,                     // default; 0 parks at once
        FirstDelay = TimeSpan.FromMinutes(1),     // default
        BackoffMultiplier = 4.0,                  // default
        MaxDelay = TimeSpan.FromHours(12),        // default
        RedeliverWhen = RedeliverWhen.TransientOrUnknown, // or Transient
    }
})
```

One processor can differ: `Processors:<id>:Redelivery:MaxRedeliveries` in configuration, or
`ProcessorExecutionOptions.Redelivery` in code. Rows that existed before upgrading to 0.7 are
parked. The handler runs again on its own, so it must be idempotent.

### Tuning the policy

```csharp
.WithControlLoop(o => o with
{
    Retry = o.Retry with
    {
        MaxRetries = 5,                           // default 3; 0 = one attempt, no retry
        RetryDelay = TimeSpan.FromSeconds(1),     // default
        BackoffMultiplier = 2.0,                  // default; 1.0 = constant delay
        MaxRetryDelay = TimeSpan.FromSeconds(30), // default backoff cap
        DeadLetterOnMaxRetries = true,            // default; false skips instead
    }
})
```

`RetryOptions` is an immutable record; use a `with` expression to change only the properties you
need: unset properties keep their current values. See
[configuration.md](configuration.md#retry-options) for the full defaults table.

`DeadLetterOnMaxRetries = false` means a failing event is **dropped silently**. Only set it for
processors where losing an event is genuinely acceptable. A negative `MaxRetries` is rejected at
startup with validation code `ALB0007`.

### Which failures are transient

`DefaultErrorClassifier` treats these as worth retrying:

- `TimeoutException`, and a `TaskCanceledException` whose token was *not* cancelled
- `SocketException`
- `DbException` with SQL state `40001` (serialization failure), `40P01` (deadlock), `57P03`
  (server starting), `08006`/`08001` (connection failure)
- `HttpRequestException` with 408, 429, 502, 503 or 504

Everything else is permanent and dead-letters on the first failure: a `JsonException` or a
`NullReferenceException` will not get better on attempt three, and retrying it just delays the
diagnosis.

Supply your own classifier by implementing `IErrorClassifier` and calling
`UseErrorClassifier<T>()` on the module builder. Do this when you talk to a system with its own
idea of "try again later": a provider that signals throttling with a 400 and a body, say.

### Clearing dead letters

```csharp
var entries = await deadLetters.GetAsync("OrderSummaryProjection");   // look first
await deadLetters.MarkForRetryAsync("OrderSummaryProjection");        // re-dispatch them
await deadLetters.ClearAsync("OrderSummaryProjection");               // throw them away
```

**`MarkForRetryAsync` re-dispatches the stored events.** It marks the entries `retry_requested`;
the application's dead-letter retry loop claims them on its next poll, under a time-bounded lease,
and runs them through the handler again. Succeed and the entry is deleted; throw and it is
*abandoned* (left in the table with the new error, no longer scheduled) so a still-broken handler
does not busy-loop. Fix the handler, deploy, then mark them again. There is at most one entry per
processor and event: an event that fails again updates its entry rather than adding a second one.

The retry loop runs in **the module's control loop**, not in the caller. Marking entries against a
module that is not running leaves them marked and waiting.

```csharp
.WithControlLoop(o => o with
{
    DeadLetterRetry = o.DeadLetterRetry with
    {
        PollingInterval = TimeSpan.FromMinutes(1),   // default
        BatchSize = 10,                              // default
        ClaimLease = TimeSpan.FromMinutes(15),       // default
    }
})
```

Set `ClaimLease` longer than your slowest handler. Too short and a healthy worker
loses its claim mid-dispatch and the event runs twice; too long and a crashed worker's entries wait
that long before another replica can pick them up.

**Rewinding past the dead letters is the bigger hammer.** Rewind the checkpoint to just before
the earliest dead letter and clear them, so the whole tail is reprocessed from the log rather than
from the dead-letter copies. Use it when the bug affected more events than the ones that actually
failed: a projection that silently mis-folded ten events before one of them threw. Read the
earliest `GlobalPosition` from `GetAsync`, call `RewindAsync` with one less, then `ClearAsync`.
**Rewind first**: the two calls are not one transaction, and a crash between them should leave
dead letters for events that will be reprocessed anyway, never cleared entries with no replay.

**`ClearAsync` throws the entries away.** No replay, no recovery. The events stay in the log but
nothing will reprocess them for that processor. Look at them first.

## Moving a checkpoint

```csharp
await checkpoints.GetAsync("OrderSummaryProjection");
await checkpoints.ResetAsync("OrderSummaryProjection");          // back to 0: replays everything
await checkpoints.RewindAsync("OrderSummaryProjection", 12345);  // to an exact position
```

Checkpoint writes from the loop are **monotonic**: the upsert uses `GREATEST`, so a processor can
never move itself backwards. `ResetAsync` and `RewindAsync` are the deliberate escape hatch and
the only way a checkpoint goes down.

Two things to have straight before using them:

- **Rewinding a projection replays its events over existing state.** For a counter projection that
  double-counts. If the projection is not idempotent under replay, you want
  [a rebuild](projections.md#rebuilding-a-projection), not a rewind.
- **Rewinding a reactor re-fires its side effects.** That is the ten-thousand-emails failure.

Moving forward is the safe direction and has a genuine use: renaming a reactor's processor id would
otherwise replay the entire log through it, so save the new id at the current head *before* the
new version starts. `SaveAsync` is monotonic, which is exactly right here:

```csharp
await checkpoints.SaveAsync("order-emails-v2", head);   // head: SELECT MAX(global_position) …
```

## Rebuilds

```csharp
await rebuilds.StartAsync("OrdersOverviewProjection", "OrdersOverviewProjection", targetPosition: head);
await rebuilds.ListAsync();
await rebuilds.RequestPromotionAsync("OrdersOverviewProjection");
await rebuilds.RequestAbortAsync("OrdersOverviewProjection");
```

Each `ProjectionRebuildState` carries its place in the state machine:

| `RebuildStatus` | Means |
|---|---|
| `Idle` | No rebuild running: none ever has, or the last one finished |
| `Rebuilding` | A shadow loop is replaying history into the new version |
| `Ready` | The shadow loop reached the target position; waiting to be promoted |
| `Completed` | Promoted; the rebuilt version is the one readers see |
| `Aborted` | Abandoned, partial state discarded |

Progress is the shadow loop's own checkpoint against the target position. A rebuild sitting at
`Rebuilding` with no progress at all has not been picked up by any application, almost always
because the module was not configured with `WithRebuilds()`.

The second argument to `StartAsync` is the projection type: the projection's `Collection(...)`
name, which is usually its processor id. The target is normally the current head.

`RequestPromotionAsync(id, force: true)` publishes a version that has *not* finished replaying: an
incomplete projection, live, to real readers. It exists for the case where you know the remaining
tail is irrelevant. It is not a way to make a slow rebuild finish.

Promotion and abort are *requests*: the module's `RebuildCoordinator` performs the handoff on its
next poll. The full mechanism, and what you must have configured for it to be safe, is in
[projections.md](projections.md#rebuilding-a-projection).

## Running more than one replica

By default every replica of your application runs its own control loop over the same log. They
all process every event. For projections that is wasteful but harmless if the projection is
idempotent; for reactors it means duplicate side effects. Turn on leases:

```csharp
.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })
// replicaId defaults to Environment.MachineName; override with Leases = o.Leases with { ReplicaId = "..." }
```

Now one replica holds each processor at a time, and the lease **fences** the checkpoint: a replica
that was partitioned away and comes back cannot overwrite a checkpoint its successor already
advanced (`IFencedCheckpointStore.SaveIfLeaseHeldAsync`).

Enabling leases is required, not optional, if you run more than one replica **and** use
rebuilds; two replicas would otherwise replay into the same shadow version.

Multi-tenant deployments have a second layer: tenants are distributed across replicas by a
consistent hash ring, each claimed by a lease in `alberto_tenant_leases`. See
[multi-tenancy.md](multi-tenancy.md#tenant-leases).

A crashed replica's tenant leases expire after `LeaseDuration` (60 s by default) and are picked up
by the survivors. To force reacquisition sooner, delete the rows; the application reacquires on
its next poll:

```sql
DELETE FROM orders.alberto_tenant_leases;                               -- every lease
DELETE FROM orders.alberto_tenant_leases WHERE consumer_id = 'worker-3'; -- one consumer's
```

## Telemetry

```csharp
services.AddAlberto("orders", builder => builder
    .WithTelemetry()
    …);
```

`.WithTelemetry()` calls `services.AddOpenTelemetry()` and adds Alberto's activity source and meter
(both named `Alberto`) to it, so no `AddSource("Alberto")` / `AddMeter("Alberto")` is needed. This
is additive: when your application also calls `AddOpenTelemetry()` the two merge into one builder.
When it does not, the SDK is installed with **no exporters**, which emits nothing until you add
one (`.UseOtlpExporter()`, or `WithTracing`/`WithMetrics` with your own). `AddAlbertoInstrumentation()`
is for standalone `TracerProvider` / `MeterProvider` wiring outside the generic host. See
[configuration.md](configuration.md#telemetry-options) for all telemetry options.

`AddOpenTelemetry()` itself comes from `OpenTelemetry.Extensions.Hosting`, which you reference
yourself.

**Activities**

| Name | Kind | Covers |
|---|---|---|
| `append {module}` | Producer | A write, from boundary check to commit. `alberto.events.count`, `alberto.events.first_position`/`last_position`, `alberto.dcb.conflict` on a conflict, `db.system.name` on Postgres only. One `alberto.event.appending` event per event: `alberto.event.id`, `alberto.event.type`, `alberto.event.tags` |
| `process {processorId}` | Consumer | One event, or one batch, through the middleware chain |
| `send {MessageType}` | Producer | One outbox publish attempt |
| `alberto.command {CommandType}` | Internal | One command's `Commit`/`TryCommit`; `alberto.command.outcome` is `committed`, `rejected`, `conflict` (retries used up) or `error`; `alberto.command.attempt` is the attempt reached |
| `alberto.load` | Internal | The boundary read of an attempt: `alberto.load.events`, `.position`, `.types`, `.tags` |
| `alberto.decide` | Internal | The decision of an attempt: `alberto.decide.outcome`, `alberto.problem.codes` on rejection |
| `alberto.read` | Client / Internal | A read from the event store: `alberto.read.after_position`, `.limit`, `.events`; `db.system.name` (and kind Client) only on Postgres |
| `alberto.projection.inline` | Internal | One inline projection run after a commit: `alberto.projection.type`, `alberto.projection.events`. A sibling of `append` under the caller's span, not a child: the append span has already ended |
| `alberto.poll` | Internal | One control-loop poll: `alberto.poll.after_position`, `alberto.poll.events`. The `alberto.read` it issues is its child |
| `alberto.checkpoint.save` | Internal | One checkpoint write: `alberto.checkpoint.position` |
| `alberto.lease.acquire` / `.renew` / `.release` | Internal | Processor-lease work of a lease-aware group: `alberto.consumer.id`, `alberto.replica.id`; acquire adds `alberto.lease.acquired` and `alberto.lease.fence_token`, renew adds `alberto.lease.renewed` and `alberto.lease.lost` |
| `alberto.rebuild` | Internal | One projection rebuild on the replica driving it, from replay start to the sweep that reclaims the discarded version: `alberto.rebuild.version`. Events: `alberto.rebuild.replay_started` (`alberto.rebuild.shadow_id`), `.ready` (`alberto.rebuild.target_position`), `.promoted` (`alberto.rebuild.discarded_version`, `alberto.rebuild.forced`), `.aborted`, `.swept` |
| `alberto.outbox.purge` | Internal | One outbox retention pass: `alberto.outbox.purge.before` |

Attributes:

| Attribute | On | Meaning |
|---|---|---|
| `messaging.system` = `alberto`, `messaging.operation.type` = `process` / `send` | consume, send | Messaging semantic conventions |
| `messaging.consumer.group.name`, `messaging.destination.name` | consume | The processor id, the module |
| `messaging.batch.message_count` | batch consume | Events in the batch |
| `messaging.message.id` | send | Outbox entry id |
| `alberto.processor.id`, `alberto.module`, `alberto.shard` | consume (`alberto.module`/`shard` also append) | `shard` only for sharded modules |
| `alberto.event.position`, `alberto.event.type` | consume | |
| `alberto.event.position.first`, `.last` | batch consume | |
| `alberto.trace.links.count`, `alberto.trace.links.dropped` | consume | Links added; batch traces that did not fit |
| `alberto.event.position` | span links | Position of the linked event |
| `alberto.tenant.id`, `alberto.outbox.attempt`, `alberto.outbox.marked_delivered` | send | Spans only; tenant ids never go on metrics |
| `error.type` | any failed span | The exception's full type name |

Spans are never marked `Ok`; a failure sets status `Error` with **no description**, `error.type`,
and an `exception` event carrying only `exception.type`. Exception messages are not recorded
anywhere on a span: they can hold SQL or PII.

The command spans are children of whatever activity is current when the command is committed, and
the append span parents under the command root.

Background spans (`alberto.poll`, `alberto.checkpoint.save`, `alberto.lease.*`, `alberto.rebuild`,
`alberto.outbox.purge`) are always **roots**, whatever activity the host was started under, and
never become the ambient activity of unrelated work: the rebuild span in particular stays open for
the whole rebuild but parents nothing. Poll and checkpoint spans are as frequent as the polling
interval; sample them down (or drop them in a sampler by name) if they crowd your backend. The
outbox relay's `send` span is likewise a root when the entry carries no trace context.

Background loops log under an `ILogger` scope keyed like the span attributes: `alberto.module`,
`alberto.processor.id` and, for a sharded module, `alberto.shard`; the relay adds
`alberto.tenant.id` per entry, and a lease-aware group `alberto.consumer.id`/`alberto.replica.id`.

The append stamps the current trace context into each event's `metadata` as W3C `traceparent`
(and `tracestate`, when non-empty), whether or not telemetry is enabled: it is the append
span's when there is one and the caller's ambient activity otherwise. Events stored by older
versions carry `_traceId`/`_spanId` instead and are still read. The consume middleware turns the
context back into the `process` span, either as its **parent** or as an **`ActivityLink`**,
chosen per processor with `TraceParenting` on `ProcessorExecutionOptions` (or
`Alberto:Modules:{key}:Processors:{id}:TraceParenting` in configuration):

| `TraceParenting` | Default for | Shape |
|---|---|---|
| `Parent` | Reactors, the outbox handler | The consume span is a child of the append trace: one trace from request to side effect. |
| `Link` | Projections | The consume span is a new root with a link back. |

Prefer `Parent` where the flow is one logical operation, because with head sampling a link makes
the two traces sampled independently (kept together roughly `rate²` of the time) while a parent is
kept or dropped with the request. Projections default to `Link` because they fold many unrelated
appends, long after the request has finished, and a trace that stays open for that long is not
useful. With no trace context on the event, either mode yields a root span.

Batch dispatch always links, whatever `TraceParenting` says: a batch has many upstream traces and a
span has one parent. It carries one link per **distinct** upstream trace, at most 32;
`alberto.trace.links.dropped` is set on the span with the number of traces that did not fit.

The outbox continues the trace: an entry copies the source event's context, and the relay wraps
each publish attempt in a `send {MessageType}` producer span, parented to that context, tagged
`messaging.message.id`, `alberto.tenant.id` and `alberto.outbox.attempt`, with `error.type` on
failure. It writes that span's `traceparent`/`tracestate` into `ExternalMessage.Metadata`, so a
transport that forwards metadata as headers carries the trace to the receiver. The span's
`alberto.outbox.marked_delivered` attribute says whether the delivered-mark landed; a failed mark
is not a failed publish.

Baggage does not cross the event log: only `traceparent`/`tracestate` are stored, so
`Baggage.Current` set at append time is not present when a processor or the relay runs.

**Metrics**

| Instrument | Watch for |
|---|---|
| `alberto.events.appended` | Write throughput, tagged `module` (and `shard`) |
| `alberto.events.processed` | Consumer throughput: compare with the above |
| `alberto.processing.errors` | Any sustained non-zero rate |
| `alberto.dead_letters` | **Alert on any increase** |
| `alberto.retries` | A rising rate means a flaky dependency |
| `alberto.concurrency.conflicts` | Tagged `kind`: `append` is a `DcbConflictException` (a rising rate means a boundary is too wide, `module` tagged); `projection` is a state-store write conflict |
| `alberto.tenant_locks_acquired`, `alberto.tenant_lock_failures` | Lease churn: tagged by `consumer.id`, **not** by tenant |
| `alberto.append.duration` | Write latency, including the advisory lock wait; tagged `module` and `outcome` (`success`, `conflict`, `error`) |
| `alberto.event.age` | Seconds between an event's `CreatedAt` and a processor starting on it: the staleness readers see, where lag counts events |
| `alberto.processing.duration` | Per-handler latency |
| `alberto.outbox.published`, `alberto.outbox.publish.failures` | Outbox relay throughput and failed attempts (failures tagged `error.type`) |
| `alberto.outbox.delivery.latency` | Seconds from outbox entry creation to successful publish; includes retries |
| `alberto.head.stalled` | Seconds the stable-head barrier has held the head back; **any sustained non-zero value** |

`alberto.head.stalled` (Postgres, tagged by `module` and, for a sharded module, `shard`) reads 0
both under normal operation and while the barrier described under [Reading lag](#reading-lag)
has held the head for under 30 seconds — a routine short-lived write transaction never shows up
here. Past 30 seconds it turns non-zero and keeps climbing with the stall's duration; at that
same point Alberto logs one warning naming the likely blocker and how to find it via
`pg_stat_activity`, and logs once more when the barrier releases. See `EnableStableHeadBarrier`
in [configuration.md](configuration.md#postgres-options) for the remedy.

Every instrument above is on the `Alberto` meter. Also present: `alberto.processor.lag` (events a
processor is behind, tagged `processor`, `module`, `shard`) and `alberto.owned_tenant_count`
(tenants a consumer holds, tagged `consumer.id`, `module`, `shard`). Metric tag keys (`module`,
`shard`, `processor`, `outcome`, `kind`) are a separate contract from span attributes and are not
namespaced.

The instruments are created through the host's `IMeterFactory` when the container has one (a
generic host does), so each host's meter is scoped to its container; outside DI they fall back to
the static `AlbertoMetrics.Meter`. Either way the meter is named `Alberto`. The three gauges drop a
series when its source goes away: a stopped control loop removes its `alberto.processor.lag`
series and a stopped head its `alberto.head.stalled` series, rather than reporting the last value
forever. `alberto.owned_tenant_count` reads 0 after a consumer releases its leases.

The four worth alerting on: **dead letters increasing**, **lag growing without bound**,
**`alberto.head.stalled` sustained above zero**, and **`alberto.concurrency.conflicts` climbing.** The last is usually a boundary drawn wider than the
rule needs, and is fixed in your query, not your infrastructure.

## Migrations

The event-store schema is DbUp scripts embedded in `Alberto.Postgres`, applied automatically at
startup:

```csharp
.WithPostgres(o => o with
{
    ConnectionString = connectionString,
    Schema = "orders",
    AutoMigrate = true,   // the default
})
```

Set `AutoMigrate = false` where a running application must not issue DDL: a least-privilege
production role, or a deployment pipeline that gates schema changes. Then run the migrations from
your own step; the [Orders example](https://github.com/codest-be/alberto-examples) does exactly this with a small migrations runner project.

A role that may run DDL on its own database but cannot connect to the server's `postgres`
maintenance database can still migrate at startup: keep `AutoMigrate = true` and set
`EnsureDatabase = false`, so the migrator skips creating the database and never leaves the one in
the connection string. The database must already exist.

Each module owns its schema. Two modules in one database are two schemas, migrated independently.

## A runbook, condensed

| Symptom | Look at | Then |
|---|---|---|
| Read model is stale | `alberto.processor.lag` for that processor | Growing → the app is failing or slow; pinned → check dead letters |
| A specific document is wrong | The document in the projection's store | Bug in `apply` → fix, deploy, `IProjectionRebuildStore.StartAsync` |
| Dead letters appeared | `IDeadLetterStore.GetAsync(<id>)` | Fix the handler, deploy, `MarkForRetryAsync(<id>)` |
| Events processed twice | The checkpoint's `updated_at` and your deploy log | A rewind or a renamed processor id. Make the handler idempotent |
| Messages not delivered | `alberto_outbox_entries` status and `claim_expires_at` | Live claim → wait; expired claim → relay recovers it automatically |
| Conflicts spiking | `alberto.concurrency.conflicts` | A boundary is too wide: narrow the query |
| One tenant is stalled | `alberto_tenant_leases` for that tenant | Stale lease → delete it, or wait out `LeaseDuration` |
| One tenant errors, the rest are fine | `ITenantShardMap.ResolveAsync(<tenant>)`, then that shard | Sharded module. The fault is in one database, not the module |
| Health check reports `Degraded` | Its `data` payload names the unreachable shards | One shard down; the others keep serving. Do not pull the instance out |
| Restored from a backup | The head vs. each checkpoint | A checkpoint ahead of the log head skips events silently. See [Backup and recovery](backup-and-recovery.md) |
