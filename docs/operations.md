# Operations

Running Alberto in production comes down to four questions:

1. **Is anything falling behind?** — checkpoint lag.
2. **Is anything stuck?** — dead letters.
3. **Who is doing the work?** — processor and tenant leases.
4. **How do I intervene?** — the `alberto` CLI.

There is no admin HTTP API and no admin package. The operator surface is the CLI, which talks
straight to Postgres.

## The CLI

```bash
dotnet tool install --global Alberto.Cli --prerelease
alberto status
```

Or run it from the repository without installing:

```bash
dotnet run --project tools/Alberto.Cli -- status
```

### Connecting

Every command resolves its connection in this order, first hit wins:

| # | Source |
|---|---|
| 1 | `--url "Host=…;Database=…"` |
| 2 | `ALBERTO_URL` |
| 3 | `.alberto/config.json`, searched upwards from the working directory |
| 4 | `DATABASE_URL` |
| 5 | `Host=localhost;Database=postgres` |

The schema comes from `--schema`, then the config file, then `public`. **A module with a custom
schema needs `--schema` on every call** — pointing the CLI at `public` on a store that lives in
`orders` reports an empty, healthy-looking system.

Schema names must be blank (meaning `public`) or contain only lowercase letters, digits and
underscores, beginning with a letter or underscore. The CLI rejects unsafe identifiers before
opening a connection.

Put it in the config file once instead:

```json
{
  "connection": { "url": "Host=localhost;Database=alberto;Username=postgres;Password=postgres" },
  "schema": "orders"
}
```

The file also accepts an `"operator"` key. It is read by `ConnectionResolver.ResolveOperator` but no
command currently uses the value — it is reserved for attributing mutations and does nothing today.

### Global options

| Option | Applies to | Does |
|---|---|---|
| `--url`, `--schema` | Everything | Connection and schema |
| `--json` | Everything | Machine-readable output — pipe it to `jq` |
| `--dry-run` | Most mutating commands | Prints what would change, changes nothing |
| `--yes` | Every mutating command | Skips the confirmation prompt — for scripts |

`--dry-run` is on `ops checkpoint reset`/`set`, all three `ops dead-letters` verbs, and
`ops rebuild start`. It is **not** on `ops rebuild promote`, `ops rebuild abort` or
`ops tenants release` — those three take `--yes` and nothing else, so read `ops rebuild status`
or `alberto tenants` first.

`--json` on every command is deliberate: the same binary you use interactively is the one your
runbook scripts and alerting checks call.

### Sharded modules

A module whose tenants are spread over several databases ([tenant
sharding](architecture/tenant-sharding.md)) needs the CLI to know about all of them. Declare them
in the config file, alongside the control database holding the tenant → shard catalog:

```json
{
  "schema": "orders",
  "module": "orders",
  "catalog": { "url": "Host=control;Database=alberto_catalog" },
  "shards": {
    "db1": { "url": "Host=one;Database=orders" },
    "db2": { "url": "Host=two;Database=orders", "schema": "orders_legacy" }
  }
}
```

`schema` per shard falls back to the top-level one. `module` must match the key the application
passes to `AddAlberto`, because it is part of the catalog's primary key — a wrong one reads an
empty table and reports no tenants, which looks like an answer rather than a misconfiguration.

**Connection strings live here and never in the database.** The catalog table stores shard *ids*
only, so a dump of it leaks no credentials and a shard can move host without a row changing.

With shards configured, two extra options appear and the rules change:

| Option | Applies to | Does |
|---|---|---|
| `--shard <id>` | Every command | Run against that one database |
| `--all-shards` | Mutating commands | Run against every one, in id order |

- **Reads fan out by default.** `status`, `system`, `processor`, `checkpoints`, `dead-letters`,
  `events`, `projections`, `tenants`, `ops checkpoint get` and `ops rebuild status` — with no
  `--shard` they cover every database and label each section with its shard id.
- **Mutations refuse by default.** Without `--shard` or `--all-shards` they stop and name the
  databases they would have touched. Rewinding a checkpoint across every tenant's database because
  a flag was forgotten is not undoable.
- **`ops checkpoint set` takes no `--all-shards`.** A position is a per-database sequence, so one
  number cannot apply to several. Name the shard.
- **A failing shard does not stop the run.** The remaining databases are still processed and the
  exit code is non-zero, so you can see which of them moved rather than only where the run stopped.
- **`--url` overrides all of it** and targets exactly that database, unlabelled.

Config with no `shards` key behaves exactly as it always has: one database, no shard labels, no
refusals. Nothing about the single-database case changed.

#### The catalog

```bash
alberto shards list                          # each shard, its tenant count, and whether config declares it
alberto shards where acme                    # which database a tenant's events are in
alberto shards assign acme --shard db2       # place a tenant — permanent
```

These read and write the control database, so they take `--url`/`--schema` pointing at *it*, plus
`--module` to override the configured module key.

`list` unions the shards your config declares with the ones the catalog actually references, and
warns about any id in the catalog that config does not declare — that shard's tenants cannot be
served by this deployment.

`where` exits non-zero for a tenant with no assignment, so a script can branch on it without
parsing the message.

**`assign` is permanent.** It is first-writer-wins: assigning a tenant that already has a shard
fails and tells you which one it is in, rather than moving it. There is no relocation command —
moving a tenant between databases means migrating its events, checkpoints, projection state and
leases across two unrelated position sequences, which is a migration you write and verify against
your own data.

## Inspecting

```bash
alberto status                       # global position, processor count, dead letters, per-processor positions
alberto system                       # the same header plus the last event's timestamp
alberto processor <id>               # one processor's checkpoint and when it last moved
alberto checkpoints                  # every checkpoint
alberto events --type order-placed --tag "order:$ID" --tenant acme --after 1000 --limit 20
alberto dead-letters --processor OrderSummaryProjection --tenant acme --limit 20
alberto projections types            # which projections have state
alberto projections list OrderSummary --tenant acme --search "ord-" --limit 50
alberto tenants                      # tenant leases: who holds what, until when
```

The inspection commands discover whether the migrated schema is single-tenant or multi-tenant.
Tenant identity appears in event, dead-letter and projection output for multi-tenant stores.
Passing `--tenant` to a single-tenant store is an error rather than a silently ignored filter, and
`alberto tenants` identifies the store as single-tenant instead of presenting an ambiguous empty
lease table.

### Reading lag

`alberto status` prints the log's **global position** in the header and each processor's **last
position** in the table. Lag is the subtraction:

```bash
alberto status --json | jq '.globalPosition as $head | .processors[] | {id: .processorId, lag: ($head - .lastPosition)}'
```

This is the number to alert on. A few hundred events of lag on a busy module is normal — the
control loop polls. Lag that only grows means the processor is failing, blocked, or slower than
the write rate; lag that is pinned at exactly the same number means it has stopped entirely
(check dead letters and whether the application is running at all).

`alberto processor <id>`'s **Updated At** answers the "stopped entirely" question directly: a
checkpoint that has not moved in minutes on a live system is a stalled processor.

On a sharded module the subtraction is **per shard**: each database has its own `position`
sequence, so a head from `db1` against a checkpoint from `db2` is a meaningless number. Fanned-out
`--json` output is an array of per-shard objects, each tagged with a `shard` field, so the query
above becomes:

```bash
alberto status --json | jq '.[] | .globalPosition as $head | {shard, lag: [.processors[] | {id: .processorId, lag: ($head - .lastPosition)}]}'
```

Unsharded output is unchanged — still the single object, with no `shard` field.

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

Retries are exhausted → the event is written to `alberto_dead_letter_events` with its error
message, stack trace and attempt count, and **the checkpoint advances past it**. A poison event
does not block the ones behind it.

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
need — unset properties keep their current values. See
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

Everything else is permanent and dead-letters on the first failure — a `JsonException` or a
`NullReferenceException` will not get better on attempt three, and retrying it just delays the
diagnosis.

Supply your own classifier by implementing `IErrorClassifier` and calling
`UseErrorClassifier<T>()` on the module builder. Do this when you talk to a system with its own
idea of "try again later" — a provider that signals throttling with a 400 and a body, say.

### Clearing dead letters

```bash
alberto dead-letters --processor OrderSummaryProjection           # look first
alberto ops dead-letters retry OrderSummaryProjection             # re-dispatch them
alberto ops dead-letters retry-rewind OrderSummaryProjection      # rewind to the earliest and replay
alberto ops dead-letters dismiss --processor OrderSummaryProjection
alberto ops dead-letters dismiss --all                            # --all is required if --processor is omitted
```

**`retry` re-dispatches the stored events.** It marks the entries `retry_requested`; the
application's dead-letter retry loop claims them on its next poll, under a time-bounded lease, and
runs them through the handler again. Succeed and the entry is deleted; throw and it is *abandoned*
— left in the table, no longer scheduled — so a still-broken handler does not busy-loop. Fix the
handler, deploy, then `retry` again.

The retry loop runs in **your application**, not in the CLI. A `retry` against a module that is not
running leaves entries marked and waiting.

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

**`retry-rewind` is the bigger hammer.** It rewinds the processor's checkpoint to just before the
earliest dead letter and clears them, so the whole tail is reprocessed from the log rather than from
the dead-letter copies. Use it when the bug affected more events than the ones that actually
failed — a projection that silently mis-folded ten events before one of them threw. It is
transactional across both tables, which is why it lives in `PostgresAdminDataAccess` rather than
being composed from two calls.

**`dismiss` throws the entries away.** No replay, no recovery — the events stay in the log but
nothing will reprocess them for that processor. Look at them first.

## Moving a checkpoint

```bash
alberto ops checkpoint get <processor-id>
alberto ops checkpoint reset <processor-id>        # back to 0 — replays everything
alberto ops checkpoint set <processor-id> 12345    # to an exact position
```

Checkpoint writes from the loop are **monotonic**: the upsert uses `GREATEST`, so a processor can
never move itself backwards. `reset` and `set` are the deliberate escape hatch (`ResetAsync` and
`RewindAsync`) — they are the only way a checkpoint goes down.

Two things to have straight before using them:

- **Rewinding a projection replays its events over existing state.** For a counter projection that
  double-counts. If the projection is not idempotent under replay, you want
  [a rebuild](projections.md#rebuilding-a-projection), not a rewind.
- **Rewinding a reactor re-fires its side effects.** That is the ten-thousand-emails failure.

`set` forward is the safe direction and has a genuine use: renaming a reactor's processor id would
otherwise replay the entire log through it, so set the new id to the current head *before* the new
version starts.

```bash
alberto status --json | jq .globalPosition                    # → 48213
alberto ops checkpoint set order-emails-v2 48213 --yes
```

Both take `--dry-run`. Use it: it prints the processor, the position and the number of rows the real
run would touch, and then does nothing.

Without `--yes`, mutating commands prompt. In a non-interactive shell — CI, a container, a pipe —
there is nothing to prompt with, so they refuse and tell you to add `--yes` rather than proceeding
unconfirmed.

## Rebuilds

```bash
alberto ops rebuild start OrdersOverviewProjection
alberto ops rebuild status
alberto ops rebuild promote OrdersOverviewProjection
alberto ops rebuild abort OrdersOverviewProjection
```

`status` shows each projection's place in the state machine:

| Status | Means |
|---|---|
| `idle` | No rebuild running — none ever has, or the last one finished |
| `rebuilding` | A shadow loop is replaying history into the new version |
| `ready` | The shadow loop reached the target position; waiting to be promoted |
| `completed` | Promoted; the rebuilt version is the one readers see |
| `aborted` | Abandoned, partial state discarded |

The **Progress** column is the shadow loop's own checkpoint against the target position. A rebuild
sitting at `rebuilding` with no progress at all has not been picked up by any application — almost
always because the module was not configured with `WithRebuilds()`.

`--projection-type` on `start` matters only when the projection's `Collection(...)` name differs
from its processor id; it defaults to the processor id.

`promote --force` publishes a version that has *not* finished replaying — an incomplete projection,
live, to real readers. It exists for the case where you know the remaining tail is irrelevant. It
is not a way to make a slow rebuild finish.

The full mechanism, and what you must have configured for it to be safe, is in
[projections.md](projections.md#rebuilding-a-projection).

## Running more than one replica

By default every replica of your application runs its own control loop over the same log — they
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
rebuilds — two replicas would otherwise replay into the same shadow version.

Multi-tenant deployments have a second layer: tenants are distributed across replicas by a
consistent hash ring, each claimed by a lease in `alberto_tenant_leases`. See
[multi-tenancy.md](multi-tenancy.md#tenant-leases).

```bash
alberto tenants                                        # who holds what
alberto ops tenants release                            # force reacquisition after a crashed replica
alberto ops tenants release --processor-id worker-3    # just that consumer's leases
```

## Telemetry

```csharp
services.AddAlberto("orders", builder => builder
    .WithTelemetry()
    …);

services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Alberto.Dcb"))
    .WithMetrics(m => m.AddMeter("Alberto.Dcb"));
```

`.WithTelemetry()` registers Alberto's activity source and meter with the OpenTelemetry hosting
integration automatically — no separate `AddAlbertoInstrumentation()` call is needed (that
extension is retained for standalone `TracerProvider` / `MeterProvider` wiring outside the generic
host, and is a harmless no-op duplicate when called alongside `.WithTelemetry()`).
Call `AddOpenTelemetry()` to configure your
exporters and subscribe to the Alberto source and meter. The meter is `Alberto.Dcb`. See
[configuration.md](configuration.md#telemetry-options) for all telemetry options.

`AddOpenTelemetry()` itself comes from `OpenTelemetry.Extensions.Hosting`, which you reference
yourself.

**Activities**

| Name | Covers |
|---|---|
| `Alberto.Append` | A write, from boundary check to commit |
| `Alberto.Consume` | One event through the middleware chain |
| `Alberto.Process` | One handler's work |

The append writes its trace and span ids into the event's `metadata` (as `_traceId` and `_spanId`),
and the consume middleware turns them back into an **`ActivityLink`** on the `Alberto.Consume`
span. A link rather than a parent, because the consumer runs minutes later on a different loop and
is not a child of the request that wrote the event — but the seam between the synchronous write and
the asynchronous consumer is still navigable in the trace, which is the thing that is otherwise
impossible to debug.

**Metrics**

| Instrument | Watch for |
|---|---|
| `alberto.events.appended` | Write throughput |
| `alberto.events.processed` | Consumer throughput — compare with the above |
| `alberto.processing.errors` | Any sustained non-zero rate |
| `alberto.dead_letters` | **Alert on any increase** |
| `alberto.retries` | A rising rate means a flaky dependency |
| `alberto.concurrency.conflicts` | `DcbConflictException`s — a rising rate means a boundary is too wide |
| `alberto.tenant_locks_acquired`, `alberto.tenant_lock_failures` | Lease churn |
| `alberto.append.duration` | Write latency, including the advisory lock wait |
| `alberto.processing.duration` | Per-handler latency |

The three worth alerting on: **dead letters increasing**, **lag growing without bound**, and
**`alberto.concurrency.conflicts` climbing** — the last is usually a boundary drawn wider than the
rule needs, and is fixed in your query, not your infrastructure.

## Migrations

The event-store schema is DbUp scripts embedded in `Alberto.Dcb.Postgres`, applied automatically at
startup:

```csharp
.WithPostgres(o => o with
{
    ConnectionString = connectionString,
    Schema = "orders",
    AutoMigrate = true,   // the default
})
```

Set `AutoMigrate = false` where a running application must not issue DDL — a least-privilege
production role, or a deployment pipeline that gates schema changes. Then run the migrations from
your own step; the Orders example does exactly this with `apps/Alberto.Orders/Alberto.Orders.Migrations`.

Each module owns its schema. Two modules in one database are two schemas, migrated independently.

## A runbook, condensed

| Symptom | Look at | Then |
|---|---|---|
| Read model is stale | `alberto status` lag for that processor | Growing → the app is failing or slow; pinned → check dead letters |
| A specific document is wrong | `alberto projections list <type> --search <id>` | Bug in `apply` → fix, deploy, `ops rebuild start` |
| Dead letters appeared | `alberto dead-letters --processor <id>` | Fix the handler, deploy, `ops dead-letters retry <id>` |
| Events processed twice | `alberto processor <id>` and your deploy log | A rewind or a renamed processor id — make the handler idempotent |
| Messages not delivered | `alberto_outbox_entries` status and `claim_expires_at` | Live claim → wait; expired claim → relay recovers it automatically |
| Conflicts spiking | `alberto.concurrency.conflicts` | A boundary is too wide — narrow the query |
| One tenant is stalled | `alberto tenants` | Stale lease → `ops tenants release` |
| One tenant errors, the rest are fine | `alberto shards where <tenant>`, then that shard | Sharded module — the fault is in one database, not the module |
| Health check reports `Degraded` | Its `data` payload names the unreachable shards | One shard down; the others keep serving — do not pull the instance out |
