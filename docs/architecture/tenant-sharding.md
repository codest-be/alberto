# Tenant sharding

> **Experimental in v1 — diagnostic `ALB9001`.**
> Tenant sharding is a preview feature. The sharding API (`AcrossPostgresDatabases`,
> `PostgresShardBuilder`, and the types listed in [Experimental surface](#experimental-surface))
> may change in a minor v1.x release; the rest of Alberto will not. Using any part of it produces
> a compiler diagnostic so you know what you are opting into. To suppress it for a project that
> has deliberately committed to sharding:
>
> ```xml
> <!-- in the project's .csproj -->
> <PropertyGroup>
>   <NoWarn>$(NoWarn);ALB9001</NoWarn>
> </PropertyGroup>
> ```
>
> Or at a single call site:
>
> ```csharp
> #pragma warning disable ALB9001 // opted into experimental sharding
> .WithTenancy(t => t.AcrossPostgresDatabases(...))
> #pragma warning restore ALB9001
> ```

Alberto's default multi-tenancy is [row-level](../multi-tenancy.md): every event, checkpoint and
projection document carries a `tenant_id`, and one database holds them all. Sharding puts a second
layer **above** that one: a module's tenants can be spread over several PostgreSQL databases, with
row-level tenancy still separating the tenants **inside** each of them.

Ten tenants in `db1` and three in `db2` is the shape this is for. So is one tenant alone in a
database of its own — that is just a shard with one row pointing at it.

This is opt-in and it is not the default. A module that never calls `AcrossPostgresDatabases`
behaves exactly as it did before sharding existed, down to the CLI's output.

## When it earns its keep

- A tenant contractually requires its data in its own database, or in a particular region.
- One tenant is large enough that its write rate is everyone else's latency.
- A blast radius you can point at: one database down costs its own tenants and nobody else.

If none of those apply, stay on one database. Sharding multiplies your connection pools, your
migrations and your operational surface by the number of shards, and it forecloses cross-tenant
reads (see [Limits](#limits)).

## The model

```
                     request (X-Tenant-Id: acme)
                                │
                                ▼
                    TenantShardResolver ──── catalog (control database)
                       │  "acme → db2"        alberto_tenant_shards
                       │                      (module_key, tenant_id) → shard_id
                       ▼
              ShardRoutingEventStore
                       │
        ┌──────────────┴──────────────┐
        ▼                             ▼
    orders#db1                    orders#db2
  ┌────────────────┐            ┌────────────────┐
  │ events         │            │ events         │
  │ checkpoints    │            │ checkpoints    │
  │ projections    │            │ projections    │
  │ leases         │            │ leases         │
  │ control loops  │            │ control loops  │
  │ WHERE tenant_id│            │ WHERE tenant_id│
  └────────────────┘            └────────────────┘
```

Three things follow from that picture, and most of the rest of this document is consequences of
them:

1. **A shard is a complete Alberto module.** Its own data source, migrations, checkpoints, dead
   letters, leases and control loops, registered under the DI key `{moduleKey}#{shardId}`.
   `ShardKey` is the only type that composes or parses that string; everywhere else it is opaque,
   which is why the rest of the codebase needed no knowledge that sharding exists.
2. **Routing composes with row-level tenancy, it does not replace it.** The router picks the
   database; the shard's own tenant decorator still filters on `tenant_id` inside it. A shard
   holding several tenants isolates them exactly as an unsharded module does.
3. **`position` is a per-database sequence.** Two shards each number their events from 1. A
   position from one shard means nothing in another, and no code — yours or Alberto's — may
   compare or union them.

## Configuring it

Sharding is declared inside `.WithTenancy(...)`, because a shard routes tenants and there is
nothing to route without them:

```csharp
services.AddTenancy();                                  // once, on the IServiceCollection

services.AddAlberto("orders", module => module
    .WithPostgres(o => o with { Schema = "orders", MaxPoolSize = 30 })
    .WithTenancy(t => t.AcrossPostgresDatabases(s => s
        .WithCatalog(o => o with { ConnectionString = catalogCs, MaxPoolSize = 5 })
        .AddShard("db1", o => o with { ConnectionString = db1Cs })
        .AddShard("db2", o => o with { ConnectionString = db2Cs, MaxPoolSize = 10 })
        .WithDefaultShard("db1"))));
```

| Call | Does |
|---|---|
| `AddShard(id, configure)` | Declares one database. `configure` transforms the module's own `.WithPostgres(...)` options, so only what actually differs per database needs writing |
| `WithCatalog(configure)` | Declares the control database holding the tenant → shard table. Required |
| `WithDefaultShard(id)` | Where a tenant with no catalog row is placed. Omit it to refuse instead |
| `WithRefreshInterval(t)` | How often the resolver re-reads the catalog. Default 30 seconds |

**Shard ids are identifiers, not hostnames.** `^[a-z][a-z0-9_]{0,62}$` — the same allowlist Alberto
applies to schema and tenant ids, because a shard id becomes a DI key, a metric tag and a lease
holder name. Name the deployment slot (`db1`, `eu`, `legacy`), not the machine; the id is written
into the catalog next to every tenant assigned to it and outlives any host you might move.

`WithRefreshInterval` is **not** how quickly a new tenant is picked up — a cache miss reads the
catalog straight away. It is how quickly a mapping *changed by another process* is noticed.

### Tuning from configuration

A shard is declared in code and tuned from configuration. Options layer in this order, each
overriding the last:

```
module code (.WithPostgres)  →  shard code (.AddShard)  →  module configuration  →  shard configuration
```

```jsonc
{
  "Alberto": {
    "Modules": {
      "orders": {
        "Postgres": { "Schema": "orders" },          // reaches every shard
        "Tenancy": {
          "DefaultShard": "db1",
          "CatalogRefreshInterval": "00:00:30",
          "Catalog": { "ConnectionString": "…" },
          "Shards": {
            "db2": { "ConnectionString": "…", "MaxPoolSize": 10 }
          }
        }
      }
    }
  }
}
```

**A shard named only in configuration is reported, never created** — `ALB0015`. Shard services are
registered while the container is still being built, which is before any configuration is read, so
a shard that first appeared here would have no data source, no migration and no control loops: a
database that silently accepts nothing. Add the `.AddShard(...)` call in code, then tune it here.

### Pool sizes multiply

`MaxPoolSize` is per shard, not per module. The example above asks for 30 connections against
`db1`, 10 against `db2` and 5 against the catalog — 45, not 30. Count the shards before you copy a
single-database pool size across all of them; the usual way to discover this is a connection
exhaustion incident on the database server, which sees the sum.

The catalog wants a modest pool of its own: it holds one small table and is read once per unknown
tenant, then served from an in-process cache.

## The catalog

One table, `alberto_tenant_shards`, in a control database:

| Column | |
|---|---|
| `module_key` | Part of the primary key, so several modules can share one control database |
| `tenant_id` | |
| `shard_id` | |

**It holds shard ids and nothing else.** What an id resolves to lives in your application
configuration, so a dump of this table leaks no credentials, and a shard can move to another host
without a row changing. The same holds for the CLI, which reads shard connection strings from
`.alberto/config.json` and never from the database.

The catalog is deliberately a separate database from any shard. Every request to a sharded module
resolves through it, and putting it inside a shard would make that shard load-bearing for routing
to all the others. `ALB0014` reports a sharded module that declares no catalog; `PostgresCatalogMigrator`
creates the table, with a journal of its own so it does not collide with an event store's migrations
if the two ever share a database.

Assignment is **first-writer-wins**: `ON CONFLICT DO NOTHING`, then read back the winner. Two hosts
seeing the same new tenant in the same instant must agree on one shard, and the loser must learn
which one won — writing a tenant's events to two databases is not recoverable.

### Resolution

`TenantShardResolver` answers "which database is this tenant in?" for every read and every append.
It serves from an immutable snapshot that a background refresh replaces wholesale; a tenant the
snapshot has not seen falls through to the catalog behind a per-tenant gate, so a burst of first
requests for one tenant produces one query rather than one per request.

The cache is an optimisation and nothing more. Resolution happens inside each async call rather
than when the store is constructed, so a cold cache costs a query, never a wrong answer.

A tenant with no row and no `WithDefaultShard` throws `UnknownTenantException`. That is the
deliberate strict mode: without a default, Alberto will not guess where a tenant belongs, because
guessing wrong writes events into a database the tenant will never be read from again.

A catalog row naming a shard this deployment does not declare throws `ShardUnavailableException`
for the tenants that reference it — an operator's stray `INSERT`, or a deploy that has not rolled
out yet, fails those tenants rather than blocking the host from starting.

## Degradation

One database being unreachable costs its own tenants and nobody else. That is the entire point of
splitting them, so nothing in Alberto escalates it into a whole-process failure:

- **A shard whose migration could not run is recorded, not fatal.** `ShardHealth` takes the
  report; the host starts and every other shard serves.
- **The health check reports `Degraded`**, not `Unhealthy`, while some shards are up. A load
  balancer that pulled the instance out over one shard would only spread the outage to the tenants
  that were fine. It reports `Unhealthy` only when *no* shard is reachable. Registered
  automatically as `alberto-shards-{moduleKey}` with tags `alberto` and `shards`; an application
  that never calls `AddHealthChecks()` pays nothing for it.
- **Requests for a down shard throw `ShardUnavailableException`**, scoped to that request.

`ShardHealth` is observation, not admission control. Nothing on the request path consults it before
trying a shard: a stale "unhealthy" would fail requests a recovered database could have served, and
a stale "healthy" would not have saved the request anyway — the attempt fails on its own and
reports here.

## Telemetry

Every metric and span a sharded module emits carries a `shard` tag alongside the existing `module`
tag; the module tag stays the logical key, so an existing dashboard keeps aggregating across shards
and a new one can break down by database. Unsharded modules emit no `shard` tag at all.

## Operating it

The CLI's rules follow from positions being per-database:

- **A read with no `--shard` fans out over every configured database** and labels each section with
  its shard id. Seeing all of them is what an operator wants from `checkpoints` or `dead-letters`.
- **A mutation with no selection refuses**, and the error names the databases it would have
  touched. Rewinding a checkpoint across every tenant's database because a flag was forgotten is
  not something you can undo. Pass `--shard <id>` or `--all-shards`.
- **`ops checkpoint set` takes no `--all-shards` at all.** A position is a per-database sequence,
  so one number cannot apply to several.
- **A failing shard does not stop the run.** Each database's state is its own, and an operator
  promoting across a fleet needs to know which of them moved, not only where the run stopped. The
  exit code is still non-zero.
- **`--url` beats the configured shards.** Naming a database explicitly and then fanning out over
  the config's shards anyway would be a surprise, and a destructive one for a mutation.

Full command reference, including the `alberto shards` group and the `.alberto/config.json`
layout, is in [operations.md](../operations.md#sharded-modules).

## Validation

Reported at startup by `AlbertoModuleValidator`, collected into one error message with everything
else:

| Code | Condition |
|---|---|
| `ALB0010` | Shards declared but not tenancy |
| `ALB0011` | Shards declared but the backend does not support tenancy |
| `ALB0012` | A shard id is not a safe identifier, or two shards share one |
| `ALB0013` | `WithDefaultShard` names a shard that was not declared |
| `ALB0014` | Shards declared but no catalog |
| `ALB0015` | Configuration declares a shard the module does not |
| `ALB0016` | Two shards resolve to the same database and schema |

`ALB0016` is worth dwelling on: two shards pointing at the same storage would each run their own
control loops over the same events, under checkpoints that each think they own the log. Separate
shards must be separate storage — a different database, or at minimum a different schema.

## Experimental surface

The following types carry `[Experimental("ALB9001")]`. Referencing any of them in code that does
not itself suppress ALB9001 produces a diagnostic. All of them are opt-in; they are unreachable
without first calling `AcrossPostgresDatabases`.

| Type | Where |
|---|---|
| `AcrossPostgresDatabases` | `Alberto.Dcb.Postgres.ShardingBuilderExtensions` |
| `PostgresShardBuilder` | `Alberto.Dcb.Postgres` |
| `PostgresTenantShardMap` | `Alberto.Dcb.Postgres` |
| `ITenantShardMap` | `Alberto.Dcb.Tenancy` |
| `TenantShardResolver` | `Alberto.Dcb.Tenancy` |
| `ShardHealth` | `Alberto.Dcb.Tenancy` |
| `ShardState` | `Alberto.Dcb.Tenancy` |
| `ShardHealthCheck` | `Alberto.Dcb.Tenancy` |
| `ShardRoutingEventStore` | `Alberto.Dcb.Tenancy` |
| `ShardRoutingEventStoreBackend` | `Alberto.Dcb.Tenancy` |
| `UnknownTenantException` | `Alberto.Dcb.Tenancy` |
| `ShardUnavailableException` | `Alberto.Dcb.Tenancy` |

`ShardKey` is **not** experimental: it is used by Alberto's telemetry layer on every module,
sharded or not, so marking it would emit ALB9001 for applications that have never opted into
sharding.

## Limits

Two of them, both deliberate, both load-bearing on how you design around this.

### A tenant cannot be moved between shards

There is no relocation command and no supported procedure. Moving a tenant means copying its
events, its checkpoints, its projection state and its leases into another database while it is
being written to, and then reconciling two per-database position sequences — a migration you write
and verify against your own data, not a button.

Assign deliberately: `alberto shards assign` is first-writer-wins precisely so an assignment is a
one-time decision rather than something a race can silently redo. Use `WithDefaultShard` when
tenant onboarding is automatic and you accept everyone landing in one place until you intervene;
omit it when placement is a deliberate act.

### There are no cross-shard reads

`StreamAllAsync` on a sharded module still returns one shard's worth of events — the current
tenant's. It cannot union: `afterPosition` is a per-database sequence, so a merged result would
order by numbers from unrelated sequences and silently skip events on the next page. Reading every
shard is a fan-out you write, with a cursor per shard.

The same holds for projections. A read model is stored in the shard that produced it, so a query
that aggregates across tenants only sees the tenants in the database it is pointed at. **A
cross-tenant dashboard is not something a sharded module can serve from one query.** Fan out in the
read layer and merge, or maintain the aggregate in a separate unsharded module fed by the outbox.

In this repository, the Orders example's `getOrdersOverview` is exactly such an aggregate. It is
not sharded and is unaffected today — but it is the shape of query that would need rewriting as a
fan-out if it were.
