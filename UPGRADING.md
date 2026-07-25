# Alberto DCB — Upgrade Notes

This file collects **every breaking change** introduced across all release cycles.
The most recent cycle (projection rebuilds) is at the top. Older changes follow.

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
`.WithControlLoop(loop => loop.WithRebuilds())` or a started rebuild sits at `rebuilding` forever.

### Also removed

`BufferedCheckpointStore` is gone. It was `internal` and never constructed, so no consumer can be
affected; `CachingCheckpointStore` is and always was the one in the pipeline.

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

**What changed:** `PostgresMigrator.Migrate()` and `PostgresMigrator.GetPendingMigrations()`
now validate the `schema` parameter against the allowlist `^[a-z][a-z0-9_]{0,62}$`. Names
that do not match throw `ArgumentException`. The internal `EnsureSchemaExists()` method now
uses a double-quoted PostgreSQL identifier in the `CREATE SCHEMA IF NOT EXISTS` DDL.

**Why:** the schema name was previously interpolated unquoted into raw DDL
(`CREATE SCHEMA IF NOT EXISTS {schema}`). A crafted schema name could execute arbitrary SQL
at startup with the service's database credentials. Severity: **critical**.

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

## Deprecations (still work, emit compiler warnings)

### Old projection API → `DeclareProjection`

```csharp
// Before (still works, CS0618 warning)
public class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderPlaced>
{
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, IEventEnvelope<OrderPlaced> envelope) { ... }
}

consumer.AddProjection<OrderSummary, OrderSummaryProjection>(...);

// After
var declaration = DeclareProjection.For<OrderSummary>()
    .WithId(e => e.ParseEvent<OrderPlaced>()?.OrderId.ToString())
    .On<OrderPlaced>((state, e) => state with { ... })
    .Build();

consumer.AddProjection(declaration, ...);
```

### Old filter API → middleware

```csharp
// Before (still works, CS0618 warning)
consumer.AddFilter<MyConsumeFilter>();

// After
consumer.WithMiddleware(ConsumeMiddlewares.RetryAndDeadLetter());
consumer.WithMiddleware(async (ctx, next) => { /* custom logic */ await next(); });
```

### `DecisionResult<TEvent>` → `Decision` / `Decision<T>`

See the Command/Result API section above for the full migration. The old type still compiles
with a CS0618 warning and will be removed in a future version.

### Standalone `AddAlbertoStore(moduleKey, assembly)` → builder chaining

See the Command/Result API section above. The standalone overload still compiles with a CS0618
warning and will be removed in a future version.
