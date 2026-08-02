# Alberto Configuration Reference

This document covers Alberto's three-phase configuration pipeline, every option record and
its defaults, how configuration overlays code, the startup validation catalog, and how to
write a custom storage backend.

For the async processing architecture (polling consumer, control loop, dead-letter loop)
see [architecture/async-processing.md](architecture/async-processing.md).

---

## The three phases

`AddAlberto(moduleKey, configure)` splits its work into three distinct phases that run in
sequence before the host starts. Understanding the split makes it clear why call order
inside the lambda does not matter and why building a service provider is always
side-effect free.

### Phase 1 — Declare

The `configure` callback runs against a fresh `DcbModuleBuilder`. Each fluent call records
intent into an immutable `AlbertoModuleDefinition`; nothing is registered and no I/O
happens. Call order inside the callback is irrelevant — the module key, backend, processor
list, and options are accumulated without touching any service.

```csharp
services.AddAlberto("orders", module => module
    // These three lines can appear in any order:
    .WithTenancy()
    .WithPostgres(o => o with { ConnectionString = cs, Schema = "orders" })
    .WithControlLoop(o => o with { BatchSize = 500 }));
```

### Phase 2 — Bind and validate

After the callback returns, `AddAlberto` registers the `AlbertoModuleDefinition` as a
**named options instance** (keyed by `moduleKey`) and attaches `ValidateOnStart()`.

At host startup, ASP.NET Core's options pipeline:

1. Binds `Alberto:Modules:{moduleKey}` from `IConfiguration` on top of the code-declared
   defaults (configuration wins — see [Precedence](#precedence-configuration-beats-code)).
2. Escalates `Checkpoints:OrphanPolicy` to `Strict` when outside `Development` and the
   value was not explicitly set in configuration (see [Checkpoint hygiene](#checkpoint-hygiene)).
3. Runs `AlbertoModuleValidator` (an `IValidateOptions<AlbertoModuleDefinition>`) which
   collects every problem and surfaces all of them in one error rather than stopping at
   the first (see [Validation catalog](#validation-catalog)).

If validation fails the host refuses to start, naming every code (`ALBxxxx`) and its
remedy.

### Phase 3 — Register

Still inside `AddAlberto`, after validation is wired up, the backend's `Register` method
and every deferred `builder.Register(...)` callback run. They receive an
`AlbertoModuleContext` whose `Definition` is the code-declared snapshot (configuration has
not been applied yet at this point — the overlay happens at first options resolution during
startup). The context exposes:

| Property | Type | Description |
|---|---|---|
| `Services` | `IServiceCollection` | The application's service collection |
| `Definition` | `AlbertoModuleDefinition` | The code-declared (pre-overlay) definition |
| `ModuleKey` | `string` | Shorthand for `Definition.ModuleKey`; use as the DI service key |
| `TenancyEnabled` | `bool` | Shorthand for `Definition.TenancyEnabled` |

Services registered here use keyed DI (`moduleKey` as the key), so multiple modules in
one application each get their own isolated service instances.

---

## Configuration key layout

Every option lives under:

```
Alberto:Modules:{moduleKey}:{Section}:{Property}
```

A complete example covering all sections:

```json
{
  "Alberto": {
    "Modules": {
      "orders": {
        "ControlLoop": {
          "PollingInterval": "00:00:00.250",
          "BatchSize": 100,
          "HeadRefreshInterval": "00:00:00.100",
          "HeadWindowSize": 2000,
          "DrainTimeout": "00:00:05",
          "Retry": {
            "MaxRetries": 3,
            "RetryDelay": "00:00:01",
            "BackoffMultiplier": 2.0,
            "MaxRetryDelay": "00:00:30",
            "DeadLetterOnMaxRetries": true
          },
          "DeadLetterRetry": {
            "PollingInterval": "00:01:00",
            "BatchSize": 10,
            "ClaimLease": "00:15:00"
          },
          "Leases": {
            "Enabled": false,
            "ReplicaId": null
          },
          "Rebuilds": {
            "Enabled": false,
            "AutoPromote": true,
            "PollingInterval": "00:00:05",
            "VersionRefreshInterval": "00:00:05"
          }
        },
        "Telemetry": {
          "Enabled": true,
          "RecordEventPayloadSize": true,
          "RecordEventTagValues": false
        },
        "Checkpoints": {
          "OrphanPolicy": "Strict"
        },
        "Postgres": {
          "ConnectionString": "Host=localhost;Database=mydb;Username=app;Password=secret",
          "AutoMigrate": true,
          "Schema": "orders",
          "MaxPoolSize": 100,
          "MinPoolSize": 0,
          "LeaseDuration": "00:01:00",
          "EnableStableHeadBarrier": true,
          "EnableNotifyListener": true
        }
      }
    }
  }
}
```

The values shown above are all defaults (except `ConnectionString` and `Schema`).

A module whose tenants are spread over several databases has one more section, `Tenancy` — see
[Tenancy and shard options](#tenancy-and-shard-options). It is absent for every other module.

---

## Precedence: configuration beats code

A value set in `appsettings.json` (or any other `IConfiguration` source) **wins over the
value declared in code**. This means you can leave the code defaults as-is and tune for a
specific environment without redeploying:

```json
// appsettings.Production.json — raises batch size in production only
{
  "Alberto": {
    "Modules": {
      "orders": {
        "ControlLoop": { "BatchSize": 2000 }
      }
    }
  }
}
```

The overlay is applied property-by-property: only properties present in configuration are
overwritten; absent properties keep the code value.

**Why not use `ConfigurationBinder` directly?** Options records in Alberto use `init`-only
properties, which `ConfigurationBinder` cannot write. The overlay instead binds into a
mutable "overrides mirror" class (e.g. `ControlLoopOverrides`) and then applies only the
non-null values onto the record with a `with` expression. Backend implementers must follow
the same pattern — see [Custom backends](#custom-backends).

---

## ControlLoop options

`builder.WithControlLoop(o => o with { ... })` · configuration path: `ControlLoop`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `PollingInterval` | `TimeSpan` | `00:00:00.250` (250 ms) | `ControlLoop:PollingInterval` |
| `BatchSize` | `int` | `100` | `ControlLoop:BatchSize` |
| `HeadRefreshInterval` | `TimeSpan` | `00:00:00.100` (100 ms) | `ControlLoop:HeadRefreshInterval` |
| `HeadWindowSize` | `int` | `2000` | `ControlLoop:HeadWindowSize` |
| `DrainTimeout` | `TimeSpan` | `00:00:05` (5 s) | `ControlLoop:DrainTimeout` |

`DrainTimeout` bounds how long shutdown waits for in-flight handlers. It applies to the control
loop, the stable-head tracker and the dead-letter retry loop. A handler that ignores its
`CancellationToken` would otherwise block `StopAsync` indefinitely — stalling host shutdown and,
with leasing enabled, holding the processor lease until it expires. When the timeout elapses the
wait is abandoned with a warning; nothing is lost, because a handler that never returns is never
checkpointed, so its event is re-delivered on the next start.

Size it above your slowest legitimate handler and below your orchestrator's termination grace
period (Kubernetes `terminationGracePeriodSeconds`, 30 s by default).

### Retry options

Nested in `ControlLoop.Retry` · configuration path: `ControlLoop:Retry`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `MaxRetries` | `int` | `3` | `ControlLoop:Retry:MaxRetries` |
| `RetryDelay` | `TimeSpan` | `00:00:01` (1 s) | `ControlLoop:Retry:RetryDelay` |
| `BackoffMultiplier` | `double` | `2.0` | `ControlLoop:Retry:BackoffMultiplier` |
| `MaxRetryDelay` | `TimeSpan` | `00:00:30` (30 s) | `ControlLoop:Retry:MaxRetryDelay` |
| `DeadLetterOnMaxRetries` | `bool` | `true` | `ControlLoop:Retry:DeadLetterOnMaxRetries` |

`BackoffMultiplier = 1.0` gives a constant delay. Values below `1.0` shrink the delay on
each retry and fail validation (ALB0007). Set `MaxRetries = 0` to disable retries.

### Dead-letter retry options

Nested in `ControlLoop.DeadLetterRetry` · configuration path: `ControlLoop:DeadLetterRetry`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `PollingInterval` | `TimeSpan` | `00:01:00` (1 min) | `ControlLoop:DeadLetterRetry:PollingInterval` |
| `BatchSize` | `int` | `10` | `ControlLoop:DeadLetterRetry:BatchSize` |
| `ClaimLease` | `TimeSpan` | `00:15:00` (15 min) | `ControlLoop:DeadLetterRetry:ClaimLease` |

### Processor lease options

Nested in `ControlLoop.Leases` · configuration path: `ControlLoop:Leases`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `Enabled` | `bool` | `false` | `ControlLoop:Leases:Enabled` |
| `ReplicaId` | `string?` | `null` (machine name) | `ControlLoop:Leases:ReplicaId` |

Set `Enabled = true` when more than one replica runs the same module; each replica
acquires a fenced lease before consuming.

### Rebuild options

Nested in `ControlLoop.Rebuilds` · configuration path: `ControlLoop:Rebuilds`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `Enabled` | `bool` | `false` | `ControlLoop:Rebuilds:Enabled` |
| `AutoPromote` | `bool` | `true` | `ControlLoop:Rebuilds:AutoPromote` |
| `PollingInterval` | `TimeSpan` | `00:00:05` (5 s) | `ControlLoop:Rebuilds:PollingInterval` |
| `VersionRefreshInterval` | `TimeSpan` | `00:00:05` (5 s) | `ControlLoop:Rebuilds:VersionRefreshInterval` |

`Enabled = true` makes the application *able* to carry out a zero-downtime projection
rebuild; nothing happens until an operator starts one with `alberto ops rebuild start`.
Set `AutoPromote = false` to park a finished rebuild at `Ready` until an operator runs
`alberto ops rebuild promote` — which is the setting you want when promotion should be a
deliberate step rather than a consequence of catching up. Both intervals must be positive
(ALB0009).

---

## Telemetry options

`.WithTelemetry(o => o with { ... })` · configuration path: `Telemetry`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `Enabled` | `bool` | `true` | `Telemetry:Enabled` |
| `RecordEventPayloadSize` | `bool` | `true` | `Telemetry:RecordEventPayloadSize` |
| `RecordEventTagValues` | `bool` | `false` | `Telemetry:RecordEventTagValues` |

A DCB tag value is a domain identifier — `order:8f21`, `customer:4471`. Append spans therefore
record only the tag **concepts** (`order,customer`) by default, which is what identifies the
consistency boundary the append was checked against. Set `RecordEventTagValues` to `true` to emit
the full `concept:id` values, and only where the collector sits inside the same trust boundary as
the database.

Exception detail is recorded as an OpenTelemetry exception **event** via `Activity.AddException`,
never as span attributes. Messages carry whatever the thrower put in them — Npgsql's include the
failing SQL — so they belong in the place collectors and backends already know how to filter.

`.WithTelemetry()` registers Alberto's activity source and meter with the OpenTelemetry
hosting integration (`services.AddOpenTelemetry().WithTracing(...).WithMetrics(...)`). If
the application never calls `AddOpenTelemetry()`, this installs the SDK with no exporters,
which is effectively inert. In a hosted application `.WithTelemetry()` does this work
automatically, so a separate `AddAlbertoInstrumentation()` call is redundant — though harmless,
because `AddSource` / `AddMeter` are idempotent. `AddAlbertoInstrumentation()` (on
`TracerProviderBuilder` / `MeterProviderBuilder`) is retained for applications that build a
`TracerProvider` or `MeterProvider` themselves, outside the generic host.

---

## Checkpoint hygiene

`.Configure(d => d with { Checkpoints = ... })` · configuration path: `Checkpoints`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `OrphanPolicy` | `OrphanCheckpointPolicy` | `Warn` in Development; `Strict` elsewhere | `Checkpoints:OrphanPolicy` |

Values for `OrphanCheckpointPolicy`:

| Value | Meaning |
|---|---|
| `Off` | Ignore checkpoints whose processor id matches no declared processor |
| `Warn` | Log a warning per orphan at startup (default in `Development`) |
| `Strict` | Fail startup and name every orphan (default outside `Development`) |

**Escalation rule.** When the host environment is not `Development`, Alberto escalates
`Warn` to `Strict` — but only when the policy was not explicitly supplied through
configuration. A `Checkpoints:OrphanPolicy = Warn` entry in `appsettings.json` is always
honoured, in every environment.

`Checkpoints` is the one section with no `With...()` builder method: it is configured
through `Alberto:Modules:{key}:Checkpoints:*` only. That is deliberate — the policy is an
operational choice that differs per environment, and the environment is exactly what
configuration is for.

**Why this matters.** A processor id is a persisted checkpoint key. Renaming a handler
class (when `processorId` is omitted from `ReactTo<TEvent, THandler>`) changes the derived
id and leaves the old checkpoint as an orphan. In `Strict` mode the startup error names
the orphaned id, giving you a chance to recover before any event is replayed.

---

## Postgres options

`.WithPostgres(o => o with { ... })` · configuration path: `Postgres`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `ConnectionString` | `string` | `""` (required) | `Postgres:ConnectionString` |
| `AutoMigrate` | `bool` | `true` | `Postgres:AutoMigrate` |
| `Schema` | `string?` | `null` (connection default) | `Postgres:Schema` |
| `MaxPoolSize` | `int` | `100` | `Postgres:MaxPoolSize` |
| `MinPoolSize` | `int` | `0` | `Postgres:MinPoolSize` |
| `LeaseDuration` | `TimeSpan` | `00:01:00` (60 s) | `Postgres:LeaseDuration` |
| `EnableStableHeadBarrier` | `bool` | `true` | `Postgres:EnableStableHeadBarrier` |
| `EnableNotifyListener` | `bool` | `true` | `Postgres:EnableNotifyListener` |

`AutoMigrate = true` runs Alberto's DbUp schema migrations at host startup via an
`IHostedService`. Setting it to `false` means you manage migrations externally (for
example, via a separate Migrations project in an Aspire sequencing setup). You can also
call `PostgresMigrator.Migrate(connectionString, schema, singleTenant)` directly — for
example from a CLI tool or design-time factory — without starting a full host.

Building a service provider never opens a database connection; all I/O is deferred to
`IHostedService.StartAsync`.

---

## Tenancy and shard options

`.WithTenancy(t => t.AcrossPostgresDatabases(s => ...))` · configuration path: `Tenancy`

Present only for a module whose tenants are spread over several databases. The mechanism is
described in [architecture/tenant-sharding.md](architecture/tenant-sharding.md); this is the
configuration surface.

| Property | Type | Default | Configuration key |
|---|---|---|---|
| Default shard | `string?` | `null` (unmapped tenants are refused) | `Tenancy:DefaultShard` |
| Catalog refresh interval | `TimeSpan` | `00:00:30` | `Tenancy:CatalogRefreshInterval` |
| Catalog database | Postgres options | — (required) | `Tenancy:Catalog:*` |
| Per-shard database | Postgres options | — | `Tenancy:Shards:{shardId}:*` |

`Tenancy:Catalog` and each `Tenancy:Shards:{shardId}` take the same properties as
[`Postgres`](#postgres-options).

```json
{
  "Alberto": {
    "Modules": {
      "orders": {
        "Postgres": { "Schema": "orders", "MaxPoolSize": 30 },
        "Tenancy": {
          "DefaultShard": "db1",
          "CatalogRefreshInterval": "00:00:30",
          "Catalog": { "ConnectionString": "Host=control;Database=alberto_catalog", "MaxPoolSize": 5 },
          "Shards": {
            "db1": { "ConnectionString": "Host=one;Database=orders" },
            "db2": { "ConnectionString": "Host=two;Database=orders", "MaxPoolSize": 10 }
          }
        }
      }
    }
  }
}
```

**Shards are declared in code and tuned here.** A shard's options layer as module code
(`.WithPostgres`) → shard code (`.AddShard`) → module configuration → shard configuration, so a
setting that is genuinely module-wide — the schema, the pool sizes — is written once at the
`Postgres` level and reaches every shard.

A shard that appears **only** here is reported as `ALB0015` and is not created. Shard services are
registered while the container is being built, before any configuration is read, so such a shard
would have no data source, no migration and no control loops.

**Pool sizes multiply.** `MaxPoolSize` is per shard: the example asks the database servers for
30 + 10 + 5 connections, not 30.

---

## Validation catalog

`AlbertoModuleValidator` is an `IValidateOptions<AlbertoModuleDefinition>` registered
under `ValidateOnStart()`. It runs at host startup and collects every problem before
surfacing them in one error message.

### Core module codes (ALB0xxx)

| Code | Condition | Remedy |
|---|---|---|
| `ALB0001` | No backend was declared | Call `.WithPostgres(...)` or `.WithInMemory()` inside `AddAlberto` |
| `ALB0002` | Two or more processors share the same id within one module | Add `[ProcessorId("...")]` to disambiguate |
| `ALB0003` | `.WithTenancy()` declared but the backend does not support tenancy | Use `.WithPostgres(...)`, which supports tenancy, or remove `.WithTenancy()` |
| `ALB0004` | A control loop duration or count is ≤ 0 (`PollingInterval`, `HeadRefreshInterval`, `DrainTimeout`, `BatchSize`, or `HeadWindowSize`) | Set a positive value in code or configuration |
| `ALB0005` | `MaxConcurrency > 1` with `BatchingMode = Disabled` — concurrency only applies within a batch | Set `BatchingMode` to `IfSupported` or `Required`, or reduce `MaxConcurrency` to 1 |
| `ALB0006` | A processor id is empty or contains whitespace | Use a non-empty identifier without whitespace |
| `ALB0007` | `Retry.MaxRetries < 0` or `Retry.BackoffMultiplier < 1.0` | Use 0 to disable retries; use 1.0 for a constant backoff delay |
| `ALB0008` | A configuration key under `Alberto:Modules:{key}` does not match any known option — for example a typo in a section or property name | Correct or remove the key; when a close match exists, the remedy shows a "Did you mean '…'?" suggestion |
| `ALB0009` | With rebuilds enabled, `Rebuilds.PollingInterval` or `Rebuilds.VersionRefreshInterval` is not a positive duration | Set a positive interval via `.WithRebuilds(pollingInterval: ...)` or the matching `ControlLoop:Rebuilds:*` key |
| `ALB0010` | The module declares shards but not tenancy — a shard routes tenants, so there is nothing to route | Declare the shards inside `.WithTenancy(t => ...)` rather than alongside it |
| `ALB0011` | The module declares shards but the backend does not support tenancy | Switch to a backend that supports it, such as `.WithPostgres(...)` |
| `ALB0012` | A shard id is not a safe identifier, or two shards share one | Use a lowercase identifier starting with a letter (maximum 63 characters), and give each `.AddShard(...)` a distinct id |
| `ALB0013` | `.WithDefaultShard(...)` names a shard that was not declared | Name a declared shard, or set `Tenancy:DefaultShard` to one |
| `ALB0014` | The module declares shards but no catalog, so there is nowhere to record which shard a tenant is in | Declare one with `.WithCatalog(o => o with { ConnectionString = ... })`, pointing at a control database rather than at one of the shards |
| `ALB0015` | Configuration declares a shard the module does not | Add `.AddShard("...", ...)` in code, or remove the `Tenancy:Shards:{id}` section — shard services are registered before configuration is read, so a configuration-only shard could never serve a request |
| `ALB0016` | Two shards resolve to the same database and schema | Give each shard its own database, or at minimum its own schema — separate shards must be separate storage |
| `ALB0017` | Module declares `.WithInMemory("sharedKey")` (sharing a backend registered by another module) together with `.WithTenancy()` | The shared in-memory backend is a singleton; it cannot carry per-tenant state for a module that declared tenancy. Either remove `.WithTenancy()` from the sharing module, or give it its own backend with `.WithInMemory()` (no shared key) |
| `ALB0018` | An event type declares `[EventType(Version = N)]` with `N > 1` but no upcaster is registered for it | Add `.AddUpcaster(DeclareUpcaster.For<T>("...").From<TOld>(1, ...).Build())` to cover versions `1..N-1`. Without an upcaster, reading any event stored before version `N` throws at runtime — `EventSerializer.Deserialize` carries the same check, so a hand-built serializer that never meets this validator refuses it too. If the bump only added optional members whose defaults are already right for older events, waive it at the declaration site instead: `[EventType("...", Version = N, UpcastingNotRequired = true)]` |
| `ALB0019` | A declared upcaster references an event type that is not registered in the module's events assembly | Ensure the type annotated with `[EventType("...")]` is in the assembly passed to `.WithEventsFrom(...)`, or remove the upcaster if the event type is no longer in use |
| `ALB0020` | An event type declares `[EventType(Version = N)]` but its upcaster chain produces a different version | If the chain stops short, add the missing step(s) so it reaches version `N` — `.From<TOld>(chainVersion, ...)` continues where the current chain stops. If it overshoots, either raise `[EventType("...", Version = chainVersion)]` to match the chain, or drop the step(s) past version `N` |
| `ALB0022` | A processor sets `BatchingMode.Required` but `MaxConcurrency > 1` — pipelined mode dispatches per-event to N workers; the Required guarantee cannot be honoured | Set `MaxConcurrency` to 1 to use batch dispatch, or change `BatchingMode` to `IfSupported` or `Disabled` |
| `ALB0023` | `.WithRebuilds()` is declared but the in-memory backend does not provide an `IProjectionRebuildStore` | Switch to `.WithPostgres(...)`, or remove `.WithRebuilds()` |
| `ALB0024` | `Leases.Enabled = true` is declared but the in-memory backend does not provide an `IProcessorLeaseManager` | Switch to `.WithPostgres(...)`, or disable leases with `.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = false } })` |
| `ALB0025` | `Leases.Enabled = true` but no `IProcessorLeaseManager` is registered under the module key, so leases can never be acquired, renewed or fenced. Only reachable through a custom `IAlbertoBackendDescriptor` — the built-in backends are covered earlier, by `ALB0024` for in-memory and by Postgres registering a manager | Register an `IProcessorLeaseManager` for the module, switch to `.WithPostgres(...)`, or disable leases |
| `ALB0026` | `AddAlberto` was called twice with the same module key | Give each module its own key. To extend a module declared elsewhere, hold onto the `DcbModuleBuilder` rather than calling `AddAlberto` again |
| `ALB0027` | `AddEfProjection` on a module that declared `.WithTenancy()`. `IProjectionEntity` has no tenant column, so the EF state store and the inline projection both load and write by `(DocumentId, RebuildVersion)` alone — two tenants producing the same document id share one row | If this declaration's ids are already unique across every tenant (a GUID aggregate id, say), state it: `AddEfProjection<TEntity, TDbContext>(declaration, documentIds: EfDocumentIdUniqueness.AcrossTenants)`. If they are not, prefix a tenant discriminator the event itself carries, give each tenant its own database with `.WithTenancy(t => t.AcrossPostgresDatabases(...))`, or use the JSONB store via `AddProjection`, whose tenancy is part of the migrated schema |

Three of these are raised outside `AlbertoModuleValidator`, because they are about
registration rather than about the declaration: `ALB0025` at host startup when the control
loop is constructed, `ALB0026` from `AddAlberto` itself — the second call throws before it
registers anything, so the first module is left intact — and `ALB0027` from
`AddEfProjection`'s deferred registration callback, which runs once the module lambda has
completed, so `.WithTenancy()` is seen whether it is chained before or after the projection.

### Store imprint (ALB0021)

Every code above validates the declaration against itself. `ALB0021` is the one that
validates it against the store it is pointed at, and it is raised by
`PostgresMigrator`, not by `AlbertoModuleValidator` — as an
`AlbertoStoreMismatchException`, before a single migration script runs.

| Code | Condition | Remedy |
|---|---|---|
| `ALB0021` | The module's tenancy declaration contradicts what the store was created as — `.WithTenancy()` added to a single-tenant store, or removed from a multi-tenant one | Point the module at a new database and replay into it, or restore the declaration the store was created with |

Single-tenant and multi-tenant are two disjoint migration sets, not a setting. There is
no in-place migration between them and no backfill for `tenant_id` on existing events,
so **`.WithTenancy()` cannot be added to or removed from a store that already has
data.** Both sets journal to the same `schemaversions` table, which is why running the
wrong one is not a no-op that a later correction undoes.

The store's mode is recorded in an `alberto_store_imprint` table, created by the
migrator itself rather than by a migration script — the check that must precede every
script cannot depend on a script having run. Where the imprint is absent, the mode is
inferred from whether `alberto_events` carries a `tenant_id` column, which covers both
stores that predate the imprint and stores left behind by a run that failed partway.
A store with no `alberto_events` table at all is fresh and may become either mode.

The diagnostic names which of the two sources disagreed with the declaration, and ends
by confirming that no scripts were run — the store is exactly as it was.

`TenancyEnabled` has no configuration overlay, so this can only ever be triggered by a
code change, never by an `appsettings` edit.

> **Not covered:** pointing a module at an *empty* database — a renamed schema, a wrong
> connection string, a lost volume. There is nothing to contradict, so it migrates
> cleanly and serves an empty store with reset checkpoints.

### Postgres codes (ALB1xxx)

Reported by `PostgresBackendDescriptor.Validate`, called from the same pass.

| Code | Condition | Remedy |
|---|---|---|
| `ALB1001` | `ConnectionString` is empty | Set it in code or via `Postgres:ConnectionString` |
| `ALB1002` | `MaxPoolSize ≤ 0` | Set a positive pool size |
| `ALB1003` | `MinPoolSize > MaxPoolSize` | Lower `MinPoolSize` or raise `MaxPoolSize` |
| `ALB1004` | `LeaseDuration ≤ 0` | Set a positive duration |
| `ALB1005` | `Schema` is not a safe lowercase PostgreSQL identifier | Use a lowercase letter followed by lowercase letters, digits, or underscores (maximum 63 characters) |

### Compile-time codes (ALB2xxx)

Every code above is raised while the host is starting. `ALB2xxx` is a different kind of
diagnostic: it comes from a Roslyn analyzer shipped inside `Alberto.Commands`, so it
appears in your build output and in your IDE, before anything runs. Referencing the
package is all that is needed — NuGet picks up `analyzers/dotnet/cs` automatically.

| Code | Condition | Remedy |
|---|---|---|
| `ALB2001` | A command pipeline is built and then discarded, which appends nothing. Every stage up to `Decide` is deferred; only `Commit`, `TryCommit` and `CommitUnconditionally` run the pipeline | Await a terminal operation. If the pipeline is being composed in steps, assign it to a variable — that is not reported. To discard one deliberately, write `_ = store.Handle(...)` |

The usual idiom is already protected by the compiler — `await store.Handle(c).Decide(f);`
does not compile, because the pipeline types are not awaitable — so what `ALB2001` catches
is the version with the `await` dropped too, where the handler returns success having
written nothing.

Severity is `warning`. To make it fail the build, or to turn it off, use an
`.editorconfig` entry like any other analyzer:

```ini
[*.cs]
dotnet_diagnostic.ALB2001.severity = error
```

---

## Custom backends

Implement `IAlbertoBackendDescriptor` to plug in a third-party event store. The five
members you must implement:

```csharp
public interface IAlbertoBackendDescriptor
{
    string Name { get; }                 // human-readable; used in validation messages
    bool SupportsTenancy { get; }        // return true to allow .WithTenancy()

    IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection);
    IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition);
    void Register(AlbertoModuleContext context);
}
```

Four more members have default implementations, so override them only if they apply:
`StorageIdentity` (returns null, which skips the two-modules-same-storage comparison),
`ApplyShardConfiguration` (defaults to `ApplyConfiguration`), `GetConfigurationSection` (returns
`(null, null)`, so the unknown-key detector `ALB0008` skips your section) and
`RegisterShardCatalog` (throws `NotSupportedException` — override it only if your backend can host
a tenant shard catalog).

A minimal implementation:

```csharp
using Alberto.Configuration;
using Microsoft.Extensions.Configuration;

namespace MyBackend;

// 1. Options record — immutable, init-only properties
public sealed record MyBackendOptions
{
    public string StorageUrl { get; init; } = "";
}

// 2. Overrides mirror — all-nullable mutable class for configuration binding
public sealed class MyBackendOverrides : IAlbertoOverrides<MyBackendOptions>
{
    public string? StorageUrl { get; set; }

    public MyBackendOptions ApplyTo(MyBackendOptions options) =>
        options with { StorageUrl = StorageUrl ?? options.StorageUrl };
}

// 3. Backend descriptor — immutable record so with-expressions work
public sealed record MyBackendDescriptor(MyBackendOptions Options)
    : IAlbertoBackendDescriptor
{
    public string Name => "MyBackend";
    public bool SupportsTenancy => false;

    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) =>
        this with
        {
            Options = AlbertoOptionsOverlay.Overlay<MyBackendOptions, MyBackendOverrides>(
                moduleSection, "MyBackend", Options),
        };

    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(Options.StorageUrl))
            yield return new AlbertoValidationFailure(
                "MYB001",
                "MyBackend has no storage URL.",
                $"Set it with .WithMyBackend(o => o with {{ StorageUrl = ... }}) " +
                $"or '{definition.ConfigurationPath}:MyBackend:StorageUrl'.");
    }

    public void Register(AlbertoModuleContext context)
    {
        context.Services.AddKeyedSingleton<IEventStoreBackend>(
            context.ModuleKey,
            (_, _) => new MyEventStoreBackend(Options.StorageUrl));
    }
}

// 4. Builder extension — the public entry point
public static class MyBackendBuilderExtensions
{
    public static DcbModuleBuilder WithMyBackend(
        this DcbModuleBuilder builder,
        Func<MyBackendOptions, MyBackendOptions> configure)
    {
        var options = configure(new MyBackendOptions());
        return builder.UseBackend(new MyBackendDescriptor(options));
    }
}
```

**Key implementation rules:**

- `ApplyConfiguration` must return a **new** descriptor instance (use `with`), not mutate
  the current one. The record is shared and may be called more than once.
- `Validate` must not open connections or resolve services — it runs before the host is
  started.
- `Register` receives an `AlbertoModuleContext`; use `context.ModuleKey` as the DI service
  key for all keyed registrations so multiple modules remain isolated.
- The overrides mirror (`MyBackendOverrides`) must be a **mutable class** (not a record)
  with all properties nullable, because `ConfigurationBinder` cannot write into
  `init`-only properties. `AlbertoOptionsOverlay.Overlay<TOptions, TOverrides>` handles
  the bind-then-apply pattern.
- Use `AlbertoValidationFailure(code, problem, remedy)` in `Validate`. Choose a code
  prefix that does not collide with Alberto's reserved ranges (`ALB0xxx`, `ALB1xxx`).

If you only need to register extra services on an existing module — not a new backend —
use `builder.Register(context => { ... })` directly instead of implementing
`IAlbertoBackendDescriptor`.
