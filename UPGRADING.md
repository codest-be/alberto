# Alberto DCB — Upgrade Notes

This file collects **every breaking change** introduced across all release cycles.
The most recent cycle (1.0 configuration pipeline) is at the top. Older changes follow.

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
| `AddAlbertoInstrumentation()` is `[Obsolete]` | A compiler warning | Delete the call; `.WithTelemetry()` registers the activity source and meter automatically |
| `.WithTelemetry()` now installs the OpenTelemetry SDK unconditionally | Nothing functionally | Nothing required. With no exporters configured there is no I/O. The registration is observable in the container — called out here rather than hidden. |
| `Checkpoints:OrphanPolicy` defaults to `Strict` outside `Development` | A deployment whose handler was renamed at any point silently replaying events now **fails at startup** | This is the intended safety net. Either carry the position with `alberto ops checkpoint rename`, pin the old id with `[ProcessorId("...")]`, or set `Alberto:Modules:{key}:Checkpoints:OrphanPolicy` explicitly in configuration. See the note below. |
| `PostgresStateStore` positional constructor argument order fixed | Code that copied `(dataSource, tenantId, projectionType, schema)` from the old samples | Switch to named arguments: `new PostgresStateStore<T>(dataSource, projectionType, schema)` |

### `OrphanPolicy` and code vs. configuration

The escalation from `Warn` to `Strict` outside Development has one asymmetry worth
knowing: an explicit `OrphanPolicy = Warn` supplied through **configuration**
(`Alberto:Modules:{key}:Checkpoints:OrphanPolicy = Warn`) is honoured and never escalated.
An explicit `Warn` supplied in **code** is escalated anyway, because `Warn` is also the
default value of `OrphanCheckpointPolicy` and Alberto cannot tell the two apart.

The configuration key is the reliable way to opt out of `Strict` in a given environment.

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
`apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs`.

---

## Summary — 2026-07-24 audit cycle

Twelve breaking changes were introduced. They fall into five areas:

| Finding | Area | Severity | What broke |
|---------|------|----------|------------|
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
catch (DbUpdateConcurrencyException ex)
{
    // Single-attempt failure not covered by the retry path
    // (e.g., external-transaction mode where ownsTransaction == false).
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

They now live on a new `IEventStoreConfigurator` interface (in `Alberto.Dcb`). The concrete
store classes (`PostgresEventStore`, `InMemoryEventStore`) implement **both** `IEventStore` and
`IEventStoreConfigurator`. `RegisterEfInlineProjection` extension methods in
`Alberto.Dcb.EntityFramework` now extend `IEventStoreConfigurator` rather than `IEventStore`.

**Why:** `IEventStore` is the runtime consumer surface. Exposing setup-only methods on it lets
runtime code accidentally register projections or handlers after the store has already started
serving requests, leading to unpredictable ordering or missed events.

**Impact:** breaking for code that calls `Register*` through a variable typed as `IEventStore`,
or that implements `IEventStore` in a custom class with those methods. Code that calls them on
concrete types directly (e.g. in tests with `new InMemoryEventStore(...)`) is **not affected**.

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
IEventStoreConfigurator configurator = new PostgresEventStore(backend);
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
