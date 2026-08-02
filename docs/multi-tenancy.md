# Multi-tenancy

Alberto's tenancy is **row-level within one schema**: every event, checkpoint and projection
document carries a `tenant_id`, and the tenant filter is applied in the SQL rather than by a
predicate application code has to remember.

It is opt-in, and it changes the shape of the store: decide before you have data.

Tenants can additionally be spread over several databases (ten in `db1`, three in `db2`) with
row-level tenancy still separating them inside each one. That is a layer above everything on this
page and is documented separately in
[architecture/tenant-sharding.md](architecture/tenant-sharding.md). Everything below describes a
module with one database, which remains the default.

## Turning it on

```csharp
services.AddTenancy();                       // once, on the application's IServiceCollection

services.AddAlberto("orders", builder => builder
    .WithTenancy()
    .WithPostgres(o => o with
    {
        ConnectionString = connectionString,
        Schema = "orders",
    })
    …);
```

**Call order does not matter.** `AddAlberto` runs the whole configuration lambda first,
accumulating every declaration into an immutable definition, and only then registers services and
reads tenancy. A `.WithTenancy()` written after `.WithPostgres(...)`, or anywhere else in the
chain, produces exactly the same result as one written before it. The backend sees the completed
definition, not a snapshot captured at the moment `.WithPostgres(...)` was called.

## Setting the tenant

`TenantContext` is **scoped**. Set it once per request, before anything touches the store:

```csharp
public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantContext tenant)
    {
        if (!http.Request.Headers.TryGetValue("X-Tenant-Id", out var value))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        tenant.SetTenant(value.ToString());   // throws on anything invalid
        await next(http);
    }
}
```

Everything downstream reads it through `ITenantAccessor`:

```csharp
string  TenantId          { get; }   // throws if no tenant is set
string? TenantIdOrDefault { get; }
bool    HasTenant         { get; }
```

### Tenant ids are constrained

`SetTenant` accepts only `^[a-z][a-z0-9_]{0,62}$`: a lowercase letter followed by up to 62
lowercase alphanumerics or underscores. Anything else throws `ArgumentException`.

This is deliberate and stricter than it looks necessary: the same allowlist governs schema names,
so a mis-configured or injected tenant id cannot become a SQL identifier. **UUIDs, hyphens and
uppercase are all rejected.** If your tenant ids are Guids today, map them to a slug at the edge.
Do not weaken the pattern.

## What isolation actually buys you

With tenancy on:

- **Reads are filtered in SQL.** `StreamAsync` becomes `StreamForTenant`, and the tenant predicate
  is part of the query, not a `.Where` you could forget.
- **`StreamAllAsync` throws inside a request.** It legitimately crosses tenants, so it is allowed
  only for the background consumer feed (which runs with no tenant set). Calling it from a
  request-scoped context (where a tenant *is* set) throws `InvalidOperationException` rather than
  quietly returning everyone's events. This guard exists because it is the exact shape of a
  real leak.
- **Projection state is per tenant; progress is not.** Each tenant's documents are keyed by tenant
  and read back under it, but a processor keeps a single checkpoint.
  `alberto_processor_checkpoints` is keyed by `processor_id` alone. One tenant's poison event
  therefore does stall the others' read models on that processor, because there is one position
  for all of them. Progress splits per tenant only under
  [tenant sharding](architecture/tenant-sharding.md), where each shard is its own database with
  its own checkpoint table.

## Tenant leases

Each tenant's processing is claimed by one replica at a time via a lease in
`alberto_tenant_leases`, renewed on a timer. Assignment is by consistent hash ring, so adding or
removing a replica moves only the tenants it has to.

```csharp
.WithPostgres(o => o with
{
    LeaseDuration = TimeSpan.FromSeconds(60),   // the default
})
```

The lease is also a **fence**. `IFencedCheckpointStore.SaveIfLeaseHeldAsync` makes a checkpoint
write conditional on still holding the lease, so a replica that was partitioned away, paused by GC
and then came back cannot overwrite the checkpoint its successor has already moved forward.

Inspect and intervene from the CLI:

```bash
alberto tenants                  # who holds what, and until when
alberto ops tenants release      # force reacquisition: after a crashed replica, say
```

`release` is transactional across the lease and assignment tables, which is why it lives in
`PostgresAdminDataAccess` rather than being composed from per-processor calls.

## Reading a projection for a tenant

The tenant is a constructor argument, not ambient, when you build a store yourself:

```csharp
new PostgresStateStore<OrdersOverview>(
    dataSource,
    projectionType: nameof(OrdersOverviewProjection),
    schema: "orders",
    rebuildVersion: ProjectionVersions.LiveVersion(sp, ModuleKey, nameof(OrdersOverviewProjection)),
    tenantId: tenantId);
```

Use named arguments. Every parameter after the data source is an optional string or delegate, and a
positional slip binds the wrong value with no compiler complaint. This bug shipped in the example
app for a while, quietly reading the wrong schema.

### EF projections have no tenant to pass

That constructor argument is the whole mechanism, and an EF projection does not have it.
`IProjectionEntity` exposes only `DocumentId` and `RebuildVersion`, so `EfStateStore` queries on
those two columns and nothing else: a `TenantId` property on your entity is invisible to it.

So an EF projection on a tenant-enabled module is correct only if two tenants can never produce the
same document id, and `AddEfProjection` refuses to register until you say so, with `ALB0027`. See
[EF projections on a tenant-enabled module](projections.md#ef-projections-on-a-tenant-enabled-module)
for the declaration and the alternatives.

## Single-tenant is not a degenerate case

Without `.WithTenancy()` you get a genuinely different backend: no tenant column in the predicates,
no leases, no ring. It is not "tenancy with one tenant", and there is no supported migration from
one to the other beyond a data migration you write.

Choose based on whether tenants share a database:

| Situation | Do |
|---|---|
| One customer, or a database per customer | Single-tenant, one module per database |
| Many customers in one database | `.WithTenancy()` |
| Many customers, one *schema* each | Single-tenant, one module per schema. `PostgresOptions.Schema` is per module |
| Many customers, spread over a few databases | `.WithTenancy(t => t.AcrossPostgresDatabases(...))`. See [tenant sharding](architecture/tenant-sharding.md) |

## Spreading tenants over several databases

The last row of that table is the sharded case. It is the same row-level tenancy, with a routing
layer above it that picks the database first:

```csharp
services.AddAlberto("orders", module => module
    .WithPostgres(o => o with { Schema = "orders" })
    .WithTenancy(t => t.AcrossPostgresDatabases(s => s
        .WithCatalog(o => o with { ConnectionString = catalogCs })
        .AddShard("db1", o => o with { ConnectionString = db1Cs })
        .AddShard("db2", o => o with { ConnectionString = db2Cs })
        .WithDefaultShard("db1"))));
```

Application code is unchanged: `IEventStore` under the module key routes to whichever database the
current tenant belongs to, and that database's own tenant filter still applies inside it.

Two things it costs you, before you reach for it: a tenant cannot be moved between databases, and
there are no cross-shard reads: an aggregate over all tenants is a fan-out you write. Both, and
the operational surface, are in
[architecture/tenant-sharding.md](architecture/tenant-sharding.md).

## Multi-tenancy and rebuilds

A rebuild replays the log for **all** tenants of that projection into the shadow version; state is
keyed by `(rebuild version, tenant, document id)`. There is no per-tenant rebuild. Size the work
accordingly: a rebuild on a busy multi-tenant module replays everything.

Also note the interaction with leases: run more than one replica with rebuilds enabled and you
need leases enabled (`.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })`), or two replicas will replay into the same version.

## The example

`apps/Alberto.Orders` is multi-tenant end to end: an `X-Tenant-Id` header interceptor, tenant
propagation into HotChocolate's resolver context, tenant-scoped projections and leases. Run it with:

```bash
dotnet run --project apps/Alberto.AppHost
```
