# Tenant sharding: database-per-tenant alongside row-level tenancy

Date: 2026-07-25
Status: Approved design, not yet implemented

## Problem

Alberto supports exactly one tenancy model: row-level isolation inside a single
PostgreSQL database. `.WithTenancy()` adds a `tenant_id` column, and
`TenantEventStoreDecorator` filters every read and stamps every write with the tenant
from `ITenantAccessor`.

Some tenants need their own database — for isolation, for data residency, or because
one tenant's volume should not sit in the same tables as everyone else's. Today that
is impossible without composing Alberto twice at the application level and writing the
routing, the operator tooling and the read-model wiring by hand.

The target is a mixed topology: ten tenants sharing `db1`, three sharing `db2`, one
alone in `db3`, with the same application code and the same operator tooling working
across all of them. Row-level tenancy remains the default and remains unchanged — this
is an opt-in on top of it, not a replacement.

## Goals

- A module can declare several PostgreSQL databases and route each tenant to one.
- A shard holds one or many tenants. Row-level isolation still applies inside it.
- Everything for a tenant — events, checkpoints, dead letters, projection state,
  outbox, rebuild metadata, leases — lives in that tenant's shard.
- Configuring this is a small, obvious addition to the existing builder, and the
  connection strings live where secrets already live: configuration.
- A module that does not declare shards registers exactly as it does today.

## Non-goals

- **Moving a tenant between shards.** Assignment happens at onboarding and is
  permanent. Splitting an existing tenant out to its own database is an offline
  operation (dump, restore, update catalog) documented as a runbook. Online
  relocation needs per-tenant write fencing, a copy protocol, position remapping and
  checkpoint rewriting — its own design, later.
- **Adding a shard without a deploy.** The set of shards is fixed at startup. The
  resolution seam is an interface (`ITenantShardMap`) so a runtime-dynamic
  implementation is a swap rather than a rewrite, but nothing in this design starts
  or stops a shard's machinery while the process runs.
- **Cross-shard queries.** The library ships no aggregate-across-databases read API.
- **Sharding a non-tenanted module.** Sharding is a tenancy feature; without a tenant
  there is nothing to route on.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Shard lifecycle | Fixed at startup, behind an async provider interface | Keeps every registration statically composed; leaves the dynamic version a swap |
| Projection state, checkpoints, dead letters | Shard-local | A shard is one restorable unit, and checkpoint+state stay atomic in one transaction |
| Tenant→shard map | Catalog table in a control database | Authoritative, queryable, and the shape a dynamic version needs |
| Shard connection strings | Configuration, keyed by shard id | Secrets stay out of database tables; a catalog row referencing an undeclared shard is a startup error |
| Tenant relocation | Out of scope | Needs write fencing and position remapping; separate design |
| Shard unreachable at startup | Degrade per shard | Blast-radius reduction is the reason to shard; fail-fast discards it |
| Schema for a single-tenant shard | The normal multi-tenant schema | One code path; the row filter simply matches everything |

## Architecture

### A shard is a module definition with a different backend descriptor

`AlbertoModuleDefinition` already carries an `IAlbertoBackendDescriptor`. A shard
declaration is `(ShardId, IAlbertoBackendDescriptor)`, and expansion runs the module's
registration once per shard with `definition.Backend` swapped for that shard's
descriptor.

This introduces no parallel storage hierarchy and keeps `Alberto.Dcb` free of
PostgreSQL types: core owns *that* tenants are split and where the catalog lives,
`Alberto.Dcb.Postgres` owns the connection details — the same split that exists today.

### Expansion happens at one site

`ServiceCollectionExtensions.AddAlberto` Phase 3 is currently:

```csharp
var context = new AlbertoModuleContext(services, final);
final.Backend?.Register(context);
foreach (var register in builder.DeferredRegistrations)
    register(context);
```

Every per-module service — backend, checkpoint store, dead-letter store, lease
manager, notify listener, migration hosted service, control loops, rebuild
coordinator, processors, middleware, interceptors, error classifiers — is registered
by replaying that list against one context keyed by one module key. Nothing in the
library reaches around it.

Expansion turns that into a loop over shards. Physical DI key is `orders#db1`,
composed and parsed through a `ShardKey` helper so no other code string-formats the
separator. When no shards are declared, the loop runs once with the module key
unchanged — the current code path, unmodified.

Each shard key gets its own named `AlbertoModuleDefinition` in `IOptionsMonitor`,
because every registration resolves settings via `.Get(moduleKey)`. It is bound from
`Alberto:Modules:orders` and then overlaid with
`Alberto:Modules:orders:Tenancy:Shards:db2`, so one shard's batch size or pool size
can be tuned without touching the others.

### What expansion produces, with no edits to the code involved

Per shard: a `ControlLoop` for each processor, over its own `EventStoreHead`, on its
own position sequence, checkpointing into its own database. Its own dead-letter store
and retry loop. Its own `RebuildCoordinator` and `ProjectionVersions`. Its own
LISTEN/NOTIFY listener and `IEventAppendedSignal`. Its own DbUp migration run. Its own
outbox and relay. Its own orphan-checkpoint service. Its own processor instances —
which is mandatory in any design, because `DeclaredAsyncProjection` caches its state
store on first use and one instance therefore cannot write to two databases.

No cross-shard coordination is needed because `position` is a per-database sequence.
Two shards are two independent position spaces: nothing to merge, nothing to order.

Two consequences worth naming:

**Shard-level work distribution is emergent.** `PostgresProcessorLeaseManager` writes
lease rows into each shard's own database, so replicas contend per (shard, processor)
independently. With two shards and two replicas, one replica can own `db1`'s
`order-summary` while the other owns `db2`'s — using the existing single-leader lease
code unchanged.

**`ConsumerDistributionMode` and `ITenantRing` are superseded and get deleted.**
`ConsumerDistributionMode`, `ITenantRing` and `PostgresTenantRing` are registered
nowhere and referenced by nothing — dead scaffolding from an earlier attempt at
distributing consumers per tenant. Sharding provides that distribution at shard
granularity. Leaving them in the tree would present two competing stories for the same
problem, one of which does not work.

**Migration ordering needs no new machinery.** Hosted services start in registration
order, and within each shard the migration service is registered by `Backend.Register`
before that shard's control loops. `db1`'s loops may start before `db2` has migrated,
but `db1`'s loops only ever touch `db1`. The catalog bootstrap registers ahead of all
shards.

### The catalog

One table in the control database:

```sql
CREATE TABLE alberto_tenant_shards (
    module_key  VARCHAR(100) NOT NULL,
    tenant_id   VARCHAR(100) NOT NULL,
    shard_id    VARCHAR(63)  NOT NULL,
    assigned_at TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_alberto_tenant_shards PRIMARY KEY (module_key, tenant_id)
);
```

`module_key` is in the primary key so one control database can serve several modules
with different topologies.

This is **not** the existing `alberto_tenants` table, which stays exactly as it is.
That table is trigger-maintained observation — which tenants have been seen in a given
database. The catalog is authoritative assignment. Deriving one from the other would
make assignment a consequence of writes rather than a precondition for them.

The resolution seam:

```csharp
public interface ITenantShardMap
{
    ValueTask<string?> ResolveAsync(string tenantId, CancellationToken ct = default);
    ValueTask<string>  AssignAsync(string tenantId, string shardId, CancellationToken ct = default);
    ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}
```

`PostgresTenantShardMap` is the default implementation. `.WithShardMap<T>()` replaces
it — the hook a runtime-dynamic version plugs into, and the reason routing tests can
use an in-memory dictionary instead of a container.

`TenantShardCache` wraps it: an immutable snapshot refreshed on an interval (default
30s), single-flight on miss, falling through to the catalog and then to auto-assign.

### Routing

The shard is resolved **inside each async call**, not at DI resolution time. DI
resolution is synchronous; `IEventStore` methods are not. Resolving lazily keeps the
cache an ordinary optimisation rather than a load-bearing component — a cold or stale
cache costs one query, not a failure.

```csharp
// scoped, registered under the logical key "orders"
async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(...)
{
    var shard = await _shards.ResolveAsync(_tenantAccessor.TenantId, ct);
    return await _inner.For(shard).AppendAsync(...);   // resolves keyed "orders#db2"
}
```

**Routing composes with row filtering, and both are required.** The router picks the
database; `TenantEventStoreDecorator` still filters `WHERE tenant_id = @tenant` inside
it. That is exactly what makes the mixed topology work — `db1` is an ordinary
row-level multi-tenant database that happens not to hold everyone. A shard with a
single tenant uses the same schema, with the filter matching every row.

The consumer-side backend (registered under `#db2:consumer`) continues to use
`ConsumerTenantAccessor` and stream all tenants — all tenants *in that shard*.

### Unmapped tenants

`WithDefaultShard(id)` decides the behaviour:

- **Set**: a tenant with no catalog row is assigned to that shard on first sight and a
  row is written. Onboarding stays zero-touch, matching current behaviour.
- **Omitted**: an unmapped tenant throws `UnknownTenantException`, so misrouting is
  impossible rather than merely unlikely.

The auto-assign write is `INSERT ... ON CONFLICT (module_key, tenant_id) DO NOTHING`
followed by a re-read of the effective row. Two replicas racing on a new tenant
converge, and a tenant an operator has already pinned to `db2` is not stolen back to
the default by a racing replica.

### Degradation

A shard that fails to migrate or connect at startup is recorded unhealthy and retried
in the background. Its control loops stay stopped. Healthy shards serve normally.
Startup succeeding no longer proves the whole topology is good, which is why per-shard
state is surfaced in health checks and in `alberto status`.

**Health is observed, not gating, on the request path.** A request always attempts its
shard, and the outcome updates the health record. Pre-blocking on a stale flag would
keep a recovered shard dead until the next probe. `ShardUnavailableException` is what a
failed attempt produces, not a pre-emptive refusal.

Most of the consumer side already tolerates this: `LeaseAwareControlLoopGroup` catches
per-processor acquisition failures and its scan timer retries on a cadence, so a shard
that comes back is picked up without new machinery. The new pieces are a `ShardHealth`
tracker, a migration service that records and retries instead of throwing, and the
health-check and CLI surfaces.

## Configuration

### Code

```csharp
services.AddAlberto("orders", module => module
    .WithPostgres(o => o with { Schema = "orders", MaxPoolSize = 30 })
    .WithTenancy(t => t.AcrossPostgresDatabases(s => s
        .WithCatalog(o => o with { ConnectionString = catalogCs })
        .AddShard("db1", o => o with { ConnectionString = db1Cs })
        .AddShard("db2", o => o with { ConnectionString = db2Cs, MaxPoolSize = 10 })
        .WithDefaultShard("db1"))));
```

`WithTenancy()` gains an overload taking `Action<TenancyBuilder>`; the existing no-arg
call is unchanged and still means one database with row-level isolation.

`AddShard` takes the same `Func<PostgresOptions, PostgresOptions>` shape as
`WithPostgres`, so there is one configuration idiom to learn. Module-level
`WithPostgres` is the **template**: `Schema = "orders"` applies to every shard, and
`db2` overrides only what it names.

### Configuration file

```json
{
  "Alberto": {
    "Modules": {
      "orders": {
        "Postgres": { "Schema": "orders" },
        "Tenancy": {
          "Catalog": { "ConnectionString": "...", "RefreshInterval": "00:00:30" },
          "DefaultShard": "db1",
          "Shards": {
            "db1": { "ConnectionString": "..." },
            "db2": { "ConnectionString": "...", "MaxPoolSize": 10 }
          }
        }
      }
    }
  }
}
```

Each shard entry binds through the existing `PostgresOverrides` /
`AlbertoOptionsOverlay` machinery, so configuration wins over code per property,
identically to every other Alberto setting.

> **Corrected during implementation.** This section originally said a shard declared
> only in configuration and never in code would work. It cannot: shard services are
> registered while the service collection is still being built, which is before any
> configuration is read, so such a shard would have no data source, no migration and no
> control loops. It is reported as `ALB0015` instead. A shard is declared in code and
> tuned from configuration.

### Pool sizing

Pool settings are **per shard**. The `MaxPoolSize` default of 100 across ten shards is
a thousand connections plus ten dedicated LISTEN connections. The default stays 100 so
unsharded behaviour is untouched; the documentation states the multiplication
explicitly and the example configuration uses a deliberately smaller per-shard value.

## Validation

New `AlbertoValidationFailure` entries, checked at startup:

- Sharding declared without `.WithTenancy()`.
- Sharding declared on a backend that does not support it.
- Duplicate shard id; shard id that is not a safe identifier.
- Shard with an empty connection string.
- `DefaultShard` naming a shard that is not declared.
- **Two shards resolving to the same host + database + schema.** This would start two
  control-loop sets with identical processor ids over one database, fighting over
  leases and checkpoints. Cheap to detect, unpleasant to diagnose.
- `ALB1001` ("the Postgres backend has no connection string") becomes shard-aware: a
  sharded module legitimately has no module-level connection string.

A catalog row naming a shard id that configuration does not declare is logged as an
error at catalog load and fails only the tenants that reference it, with
`ShardUnavailableException`. It does not fail host startup — an operator's bad `INSERT`
should not be able to block a deploy, and the blast radius is already the right size.

## Telemetry

Every metric, log scope and span currently tagged `module` gains a `shard` tag. The
tag is split from the physical key at construction rather than emitted as
`orders#db2`, so `module` keeps reading `orders` and existing dashboards keep working.
Unsharded modules emit no `shard` tag at all, rather than a `"default"` placeholder.

Health checks report healthy when every shard is up, degraded when some are, unhealthy
when none are.

## Operator CLI

`.alberto/config.json` grows a `shards` map mirroring the application's, and every
command gains `--shard <id>`.

- **Reads** with no `--shard` fan out across all configured shards and add a `Shard`
  column: `status`, `checkpoints`, `dead-letters list`, `events`, `projections`.
- **Mutations** require an explicit `--shard` or `--all-shards`: `checkpoints rewind`,
  `dead-letters retry`, `ops rebuild start|promote|abort`, `ops tenant`. A rewind
  silently applied to every database is the kind of thing that should have to be said
  out loud.
- A new `alberto shards` group carries the catalog-backed subcommands: `list` (shard,
  tenant count, whether config declares it), `where <tenant>`, `assign <tenant> --shard db2`.
  Implemented as its own top-level group rather than under `tenants`, which already means
  tenant leases.
- `shards assign` is refused when the tenant already has events in a different shard.
  That is relocation, which is out of scope, and remapping the catalog would strand
  the existing data rather than move it.

## Cross-shard reads

Out of the library, per the shard-local decision.

The Orders example's `getOrdersOverview` reads one `PostgresStateStore` and under
sharding would see one shard's tenants. The example gets a small fan-out helper so it
demonstrates something true; the library ships no general cross-shard query API.

Related but explicitly not fixed here: the known gap where `OrderQueries` and
`PaymentQueries` build their state store with `tenantId:` set while the writers in
`OrdersModule`/`PaymentsModule` do not. Sharding neither causes nor cures it. It does
change what fixing it later means — if those readers drop `tenantId` to become
honestly cross-tenant, under sharding they become cross-tenant *within a shard*. The
documentation notes the interaction; the gap stays where it is.

## Testing

Two databases on the existing templated Postgres cluster, not two containers.

Unit:

- `ShardKey` compose and parse round-trip, including keys containing the separator.
- Each validation failure fires on the configuration that should trigger it.
- `TenantShardCache`: hit, miss falling through to catalog, single-flight under
  concurrent misses, refresh picking up an operator's reassignment.
- Auto-assign race: two concurrent first-writes for one tenant converge on one shard;
  a pre-pinned tenant is not stolen by the default.
- The router resolves to the correct inner backend for a given tenant.
- `UnknownTenantException` when no default shard is configured.

Integration (3 tenants in `db1`, 2 in `db2`):

- Events land in the assigned database and nowhere else.
- A tenant's reads never return another shard's rows, in either direction.
- Each shard's control loop checkpoints independently; positions do not interfere.
- Projection state, dead letters and rebuild metadata land shard-local.
- Stopping one shard's control loop does not stall the other's.
- A shard unreachable at startup degrades: the host starts, the healthy shard serves,
  health reports degraded, and the shard is picked up when it returns.
- **An unsharded module registers byte-identically to today** — same service keys,
  same lifetimes, same hosted services. This is the regression guard that keeps the
  standard path from drifting.

## Files affected

New in `src/Alberto.Dcb`:

- `Configuration/TenancyDefinition.cs` — shards, catalog, default shard
- `Tenancy/TenancyBuilder.cs`, `Tenancy/ShardKey.cs`, `Tenancy/ITenantShardMap.cs`,
  `Tenancy/TenantShardCache.cs`, `Tenancy/ShardHealth.cs`
- `Tenancy/ShardRoutingEventStoreBackend.cs`
- `Tenancy/UnknownTenantException.cs`, `Tenancy/ShardUnavailableException.cs`

Modified in `src/Alberto.Dcb`:

- `ServiceCollectionExtensions.cs` — Phase 3 expansion loop; `CopyInto` carries tenancy
- `DcbModuleBuilder.cs` — `WithTenancy(Action<TenancyBuilder>)` overload
- `Configuration/AlbertoModuleDefinition.cs` — tenancy definition
- `Configuration/AlbertoModuleValidator.cs` — new failures
- `Telemetry/AlbertoMetrics.cs` and control-loop tagging — `shard` tag

Deleted from `src/Alberto.Dcb`:

- `Subscriptions/ConsumerDistributionMode.cs`, `Subscriptions/ITenantRing.cs`

New in `src/Alberto.Dcb.Postgres`:

- `ShardingBuilderExtensions.cs` — `AcrossPostgresDatabases`, `AddShard`,
  `WithCatalog`, `WithDefaultShard`
- `PostgresTenantShardMap.cs`, catalog migration script and bootstrap

Modified in `src/Alberto.Dcb.Postgres`:

- `PostgresBackendDescriptor.cs` — shard-aware `ALB1001`
- `AlbertoMigrationHostedService.cs` — record and retry instead of throw

Deleted from `src/Alberto.Dcb.Postgres`:

- `PostgresTenantRing.cs`

`tools/Alberto.Cli`: `ConnectionResolver.cs` and the config-file model gain shards;
every command gains `--shard`; `TenantsCommand` gains catalog subcommands.

Docs: `docs/multi-tenancy.md` extended; new `docs/architecture/tenant-sharding.md`.

## Future work

- Online tenant relocation between shards.
- Runtime-dynamic shard addition, implemented behind `ITenantShardMap` and a
  supervisor that starts and stops per-shard loop groups.
- A cross-shard read helper, if aggregate dashboards turn out to need one often enough
  to be worth a supported API.
