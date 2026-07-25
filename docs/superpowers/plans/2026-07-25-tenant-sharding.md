# Tenant Sharding Implementation Plan

> **For agentic workers:** Implement task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> Spec: [2026-07-25-tenant-sharding-design.md](../specs/2026-07-25-tenant-sharding-design.md)

**Goal:** Let one Alberto module route its tenants across several PostgreSQL databases, while a
module that declares no shards registers exactly as it does today.

**Architecture:** A shard is an `(id, IAlbertoBackendDescriptor)` pair. `AddAlberto` Phase 3 replays
the module's registrations once per shard under the DI key `module#shard`, so every per-module
service — backend, checkpoints, dead letters, leases, control loops, migrations — fans out with no
edits to the code that registers it. A catalog table in a control database maps tenant to shard, and
a scoped router under the logical key resolves the shard inside each async call.

**Tech Stack:** .NET 10, Npgsql 10, DbUp, xUnit v3, FluentAssertions, Testcontainers.

## Global Constraints

- `position` is a per-database sequence. Never compare or order positions across shards.
- A module with no shards must register byte-identically to today: same keys, same lifetimes,
  same hosted services. Task 12 guards this.
- Shard-local storage: events, checkpoints, dead letters, projection state, outbox, rebuild
  metadata and leases all live in the tenant's shard.
- A single-tenant shard uses the normal multi-tenant schema. Never select the `SingleTenant`
  migration folder for a shard.
- Secrets stay in configuration. The catalog stores `shard_id`, never a connection string.
- Health is observed, not gating, on the request path.
- Physical DI key format is `module#shard`, composed and parsed only through `ShardKey`.

---

### Task 1: Shard declaration model in core

**Files:**
- Create: `src/Alberto.Dcb/Tenancy/ShardKey.cs`
- Create: `src/Alberto.Dcb/Configuration/TenancyDefinition.cs`
- Create: `src/Alberto.Dcb/Tenancy/TenancyBuilder.cs`
- Modify: `src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`
- Modify: `src/Alberto.Dcb/DcbModuleBuilder.cs`
- Modify: `src/Alberto.Dcb/ServiceCollectionExtensions.cs` (`CopyInto`)
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardKeyTests.cs`
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardDeclarationTests.cs`

**Interfaces produced:**

```csharp
public static class ShardKey
{
    public const char Separator = '#';
    public static string Compose(string moduleKey, string shardId);
    public static bool TryParse(string key, out string moduleKey, out string shardId);
    public static bool IsValidShardId(string shardId);   // same rules as SchemaQualifier.IsValidName
}

public sealed record ShardDeclaration(string ShardId, IAlbertoBackendDescriptor Backend);

public sealed record TenancyDefinition
{
    public ImmutableArray<ShardDeclaration> Shards { get; init; } = [];
    public string? DefaultShardId { get; init; }
    public IAlbertoBackendDescriptor? Catalog { get; init; }
    public TimeSpan CatalogRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
    public Type? ShardMapType { get; init; }
    public bool IsSharded => !Shards.IsDefaultOrEmpty;
}

public sealed class TenancyBuilder
{
    public TenancyBuilder Configure(Func<TenancyDefinition, TenancyDefinition> configure);
    public TenancyBuilder WithShardMap<TMap>() where TMap : class, ITenantShardMap;
}
```

`AlbertoModuleDefinition` gains `public TenancyDefinition Tenancy { get; internal set; } = new();`
`DcbModuleBuilder` gains `WithTenancy(Action<TenancyBuilder> configure)` which sets
`TenancyEnabled = true` and applies the builder. The existing no-arg `WithTenancy()` is untouched.
`CopyInto` copies `Tenancy`.

- [ ] Write `ShardKeyTests`: compose produces `orders#db1`; parse round-trips; parse rejects a key
      with no separator; parse of `orders#db#1` fails rather than guessing; `IsValidShardId` rejects
      uppercase, leading digits, and names over 63 characters.
- [ ] Write `ShardDeclarationTests`: `WithTenancy(t => ...)` sets `TenancyEnabled`; declarations are
      recorded in order; `WithTenancy()` with no argument leaves `Tenancy.IsSharded` false.
- [ ] Run both, verify they fail to compile.
- [ ] Implement `ShardKey`, `TenancyDefinition`, `ShardDeclaration`, `TenancyBuilder`, and the
      `AlbertoModuleDefinition`/`DcbModuleBuilder`/`CopyInto` changes.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardKey|FullyQualifiedName~ShardDeclaration"`.
- [ ] Commit: `feat(core): declare tenant shards on the module builder`.

---

### Task 2: Postgres sharding builder extensions and configuration binding

**Files:**
- Create: `src/Alberto.Dcb.Postgres/ShardingBuilderExtensions.cs`
- Create: `src/Alberto.Dcb.Postgres/PostgresShardBuilder.cs`
- Modify: `src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs` (`ApplyConfiguration`)
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardConfigurationTests.cs`

**Interfaces produced:**

```csharp
public static class ShardingBuilderExtensions
{
    public static TenancyBuilder AcrossPostgresDatabases(
        this TenancyBuilder builder, Action<PostgresShardBuilder> configure);
}

public sealed class PostgresShardBuilder
{
    public PostgresShardBuilder AddShard(string shardId, Func<PostgresOptions, PostgresOptions> configure);
    public PostgresShardBuilder WithCatalog(Func<PostgresOptions, PostgresOptions> configure);
    public PostgresShardBuilder WithDefaultShard(string shardId);
    public PostgresShardBuilder WithRefreshInterval(TimeSpan interval);
}
```

`AddShard` starts from the module's declared `PostgresOptions` as a template, so
`WithPostgres(o => o with { Schema = "orders" })` applies to every shard. Because the module's
backend may be declared after `WithTenancy` in the chain, the template is applied at expansion time
(Task 6) rather than at `AddShard` time: `AddShard` stores the `Func<PostgresOptions, PostgresOptions>`
and expansion evaluates it against the module's options.

Configuration binding: `ApplyConfiguration` overlays `Tenancy:Shards:{id}` (as `PostgresOverrides`),
`Tenancy:Catalog`, `Tenancy:DefaultShard` and `Tenancy:CatalogRefreshInterval`. A shard id present
in configuration but not in code is **added**; one present in both is overlaid.

- [ ] Write `ShardConfigurationTests`: code-declared shards inherit the module's schema; a per-shard
      `MaxPoolSize` overrides the template; a configuration-only shard appears in the definition; a
      configuration value beats the code value for the same property; `DefaultShard` binds.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardConfiguration"`.
- [ ] Commit: `feat(postgres): configure tenant shards in code and configuration`.

---

### Task 3: Startup validation

**Files:**
- Modify: `src/Alberto.Dcb/Configuration/AlbertoModuleValidator.cs`
- Modify: `src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs` (`Validate`)
- Test: `tests/Alberto.Dcb.Tests/Configuration/ShardValidationTests.cs`

Failures to add (core, `ALB0009`–`ALB0014`):

| Code | Condition |
|---|---|
| ALB0009 | Shards declared without `.WithTenancy()` |
| ALB0010 | Shards declared on a backend where `SupportsTenancy` is false |
| ALB0011 | Duplicate shard id, or a shard id that is not a safe identifier |
| ALB0012 | `DefaultShard` names a shard that is not declared |
| ALB0013 | Two shards resolve to the same host + port + database + schema |
| ALB0014 | Sharding declared with no catalog |

`PostgresBackendDescriptor.Validate` gets a `ForShard` flag so `ALB1001` (no connection string) is
raised for a *shard* with no connection string, and suppressed for the module-level template of a
sharded module. Each shard's descriptor is validated in its own right, so `ALB1002`–`ALB1005` apply
per shard with the shard id in the message.

- [ ] Write `ShardValidationTests`, one test per code, asserting on the code and that the remedy
      text names the offending shard.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardValidation"`.
- [ ] Commit: `feat(core): validate shard declarations at startup`.

---

### Task 4: The catalog store

**Files:**
- Create: `src/Alberto.Dcb/Tenancy/ITenantShardMap.cs`
- Create: `src/Alberto.Dcb/Tenancy/UnknownTenantException.cs`
- Create: `src/Alberto.Dcb/Tenancy/ShardUnavailableException.cs`
- Create: `src/Alberto.Dcb.Postgres/PostgresTenantShardMap.cs`
- Create: `src/Alberto.Dcb.Postgres/Migrations/Catalog/001_TenantShardCatalog.sql`
- Create: `src/Alberto.Dcb.Postgres/PostgresCatalogMigrator.cs`
- Test: `tests/Alberto.Dcb.Tests/Tenancy/PostgresTenantShardMapTests.cs`

```csharp
public interface ITenantShardMap
{
    ValueTask<string?> ResolveAsync(string tenantId, CancellationToken ct = default);
    ValueTask<string>  AssignAsync(string tenantId, string shardId, CancellationToken ct = default);
    ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}
```

`AssignAsync` is `INSERT ... ON CONFLICT (module_key, tenant_id) DO NOTHING` followed by a
`SELECT`, and returns the *effective* shard id — which may differ from the requested one when
another replica or an operator won the race.

`PostgresCatalogMigrator.Migrate(connectionString, schema)` runs the `Migrations.Catalog` folder
through DbUp with its own journal table, mirroring `PostgresMigrator`.

- [ ] Write `PostgresTenantShardMapTests` against a cloned database: resolve returns null for an
      unknown tenant; assign then resolve round-trips; assign twice keeps the first winner and
      returns it; `GetAllAsync` returns only the rows for this module key; two modules can hold the
      same tenant id on different shards.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~PostgresTenantShardMap"`.
- [ ] Commit: `feat(postgres): add the tenant-to-shard catalog`.

---

### Task 5: Resolution, caching and auto-assignment

**Files:**
- Create: `src/Alberto.Dcb/Tenancy/TenantShardResolver.cs`
- Create: `src/Alberto.Dcb/Tenancy/TenantShardCacheRefresher.cs` (hosted service)
- Test: `tests/Alberto.Dcb.Tests/Tenancy/TenantShardResolverTests.cs`

```csharp
public sealed class TenantShardResolver
{
    public TenantShardResolver(
        ITenantShardMap map, IReadOnlySet<string> declaredShards,
        string? defaultShardId, ILogger<TenantShardResolver>? logger = null);

    public ValueTask<string> ResolveAsync(string tenantId, CancellationToken ct = default);
    public Task RefreshAsync(CancellationToken ct = default);
}
```

Behaviour: snapshot hit returns immediately. Miss goes to the catalog under a per-tenant
single-flight gate. Still unmapped and a default shard is configured → `AssignAsync` and cache the
effective result. Still unmapped and no default → `UnknownTenantException`. A resolved shard id that
is not in `declaredShards` → `ShardUnavailableException`, logged once per shard id.

`TenantShardCacheRefresher` calls `RefreshAsync` on the configured interval, replacing the snapshot
wholesale so an operator's reassignment is picked up.

- [ ] Write `TenantShardResolverTests` with an in-memory `ITenantShardMap`: cache hit does not touch
      the map; miss falls through and caches; concurrent misses for one tenant produce exactly one
      map call; unknown tenant with a default is assigned and the assignment is written; unknown
      tenant with no default throws `UnknownTenantException`; a catalog row naming an undeclared
      shard throws `ShardUnavailableException`; `RefreshAsync` picks up a reassignment; a losing
      auto-assign race adopts the winner's shard.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~TenantShardResolver"`.
- [ ] Commit: `feat(core): resolve tenants to shards with caching and auto-assignment`.

---

### Task 6: Phase 3 expansion

**Files:**
- Modify: `src/Alberto.Dcb/ServiceCollectionExtensions.cs`
- Create: `src/Alberto.Dcb/Configuration/ShardExpansion.cs`
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardExpansionTests.cs`

`ShardExpansion.Expand(definition)` returns the per-shard definitions: for each declared shard, the
module definition with `ModuleKey = ShardKey.Compose(moduleKey, shardId)` and `Backend` set to that
shard's descriptor. `AddAlberto` then registers named options and replays Phase 3 for each.

Per-shard named options bind from `Alberto:Modules:{moduleKey}` (the logical path) and then overlay
`Alberto:Modules:{moduleKey}:Tenancy:Shards:{shardId}`. `ConfigurationPath` stays logical, so
`ALB0008` unknown-key reporting is not duplicated per shard.

- [ ] Write `ShardExpansionTests`: a two-shard module registers `IEventStoreBackend`,
      `ICheckpointStore`, `IDeadLetterStore` and `IProcessorLeaseManager` under both shard keys; each
      shard's `NpgsqlDataSource` uses its own connection string; a per-shard `ControlLoop` batch size
      from configuration reaches only that shard; **an unsharded module produces exactly the service
      descriptors it produces on `main`** (compare descriptor keys, service types and lifetimes).
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardExpansion"`.
- [ ] Commit: `feat(core): expand a sharded module into per-shard registrations`.

---

### Task 7: Routing

**Files:**
- Create: `src/Alberto.Dcb/Tenancy/ShardRoutingEventStore.cs`
- Create: `src/Alberto.Dcb/Tenancy/ShardRoutingEventStoreBackend.cs`
- Modify: `src/Alberto.Dcb/ServiceCollectionExtensions.cs` (register the router under the logical key)
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardRoutingTests.cs`

Routing happens at the `IEventStore` level, not the backend level: the shard-keyed `IEventStore`
already has that shard's inline projections and post-append handlers attached, and those instances
must stay shard-local. The router resolves the shard inside each async call and delegates the whole
call to `sp.GetRequiredKeyedService<IEventStore>(ShardKey.Compose(moduleKey, shardId))`.

A `ShardRoutingEventStoreBackend` is registered under the logical key for parity, delegating the
same way, so code that resolves the backend directly still works.

Both are scoped, and resolve the inner service from the current scope so the request's
`ITenantAccessor` still governs row filtering inside the shard.

- [ ] Write `ShardRoutingTests` with a fake resolver and two in-memory stores: an append for a
      db1 tenant reaches the db1 store and not db2; a read for a db2 tenant reaches db2; a tenant
      with no accessor throws the existing "no tenant context" error unchanged.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardRouting"`.
- [ ] Commit: `feat(core): route tenant operations to their shard`.

---

### Task 8: Per-shard degradation

**Files:**
- Create: `src/Alberto.Dcb/Tenancy/ShardHealth.cs`
- Create: `src/Alberto.Dcb/Tenancy/ShardHealthCheck.cs`
- Modify: `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs`
- Test: `tests/Alberto.Dcb.Tests/Tenancy/ShardHealthTests.cs`

```csharp
public sealed class ShardHealth
{
    public void Report(string shardId, Exception? failure);   // null clears
    public ShardState Get(string shardId);
    public IReadOnlyDictionary<string, ShardState> All { get; }
}

public sealed record ShardState(string ShardId, bool Healthy, string? LastError, DateTimeOffset LastChangedAt);
```

`AlbertoMigrationHostedService` gains an optional `ShardHealth` and a shard id. When present, a
migration or connection failure is reported and logged instead of thrown, and a background retry
runs on the catalog refresh interval until it succeeds. When absent — the unsharded case — the throw
is unchanged.

`ShardHealthCheck` reports Healthy when all shards are up, Degraded when some are, Unhealthy when
none are.

- [ ] Write `ShardHealthTests`: a shard whose migration throws is recorded unhealthy and the host
      still starts; the healthy shard's control loops start; a retry that succeeds clears the state;
      the health check maps all/some/none to Healthy/Degraded/Unhealthy; an unsharded module with a
      bad connection string still fails startup.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardHealth"`.
- [ ] Commit: `feat(core): degrade per shard instead of failing startup`.

---

### Task 9: Telemetry

**Files:**
- Modify: `src/Alberto.Dcb/Telemetry/AlbertoMetrics.cs`
- Modify: `src/Alberto.Dcb/Subscriptions/ControlLoop.cs` (tag source)
- Test: `tests/Alberto.Dcb.Tests/Telemetry/ShardTelemetryTests.cs`

Where a physical key is used as a tag, split it with `ShardKey.TryParse` and emit `module` plus
`shard`. An unsharded module emits no `shard` tag at all.

- [ ] Write `ShardTelemetryTests`: a sharded control loop emits `module=orders` and `shard=db2`; an
      unsharded one emits `module=orders` and no `shard` tag.
- [ ] Run, verify failure.
- [ ] Implement.
- [ ] Run `dotnet test --filter "FullyQualifiedName~ShardTelemetry"`.
- [ ] Commit: `feat(telemetry): tag metrics with the shard`.

---

### Task 10: Delete superseded scaffolding

**Files:**
- Delete: `src/Alberto.Dcb/Subscriptions/ConsumerDistributionMode.cs`
- Delete: `src/Alberto.Dcb/Subscriptions/ITenantRing.cs`
- Delete: `src/Alberto.Dcb.Postgres/PostgresTenantRing.cs`

These are registered nowhere and referenced by nothing. Sharding provides consumer distribution at
shard granularity, so leaving them would present two stories for one problem, one of which does not
work.

- [ ] Confirm with `grep -rn "ConsumerDistributionMode\|ITenantRing\|PostgresTenantRing" src tools apps tests`
      that the only hits are the definitions themselves.
- [ ] Delete the three files.
- [ ] Run `dotnet build`.
- [ ] Commit: `refactor: remove the unwired tenant-ring scaffolding`.

---

### Task 11: Operator CLI

**Files:**
- Modify: `tools/Alberto.Cli/ConnectionResolver.cs`, `tools/Alberto.Cli/ConfigFileFinder.cs`
- Modify: every command under `tools/Alberto.Cli/Commands/`
- Create: `tools/Alberto.Cli/ShardResolver.cs`
- Modify: `tools/Alberto.Cli/Commands/TenantsCommand.cs`

`.alberto/config.json` grows `"shards": { "db1": { "url": "...", "schema": "orders" } }` and
`"catalog": { "url": "..." }`. `ShardResolver` turns `--shard`/`--all-shards` plus the config into
the list of (shardId, connectionString, schema) a command should run against.

- Reads with no `--shard` fan out over all shards and add a `Shard` column.
- Mutations require `--shard` or `--all-shards`; without either they exit non-zero with a message
  naming the shards they would have touched.
- A new top-level `shards` group carries the catalog verbs: `shards list` (shard, tenant count,
  whether config declares it); `shards where <tenant>` prints the assignment; `shards assign
  <tenant> --shard db2` writes the catalog and refuses when the tenant already has events in a
  different shard. It is its own group rather than a verb under `tenants`, which already means
  tenant leases.
- With no `shards` configured, every command behaves exactly as it does now.

- [ ] Implement `ShardResolver` and its unit tests (fan-out list, mutation guard, no-shards
      passthrough).
- [ ] Thread `--shard` through the commands.
- [ ] Run `dotnet build tools/Alberto.Cli/Alberto.Cli.csproj` and
      `dotnet run --project tools/Alberto.Cli -- --help`.
- [ ] Commit: `feat(cli): make operator commands shard-aware`.

---

### Task 12: Two-database integration tests

**Files:**
- Create: `tests/Alberto.Dcb.Tests/Tenancy/ShardedPostgresFixture.cs`
- Create: `tests/Alberto.Dcb.Tests/Tenancy/ShardedEventStoreTests.cs`
- Create: `tests/Alberto.Dcb.Tests/Regression/UnshardedRegistrationParityTests.cs`

`ShardedPostgresFixture` clones `PostgresTemplates.MultiTenant` twice plus one plain database for
the catalog, so this costs three databases on the existing shared cluster and no new container.

Assertions: events for a db1 tenant appear only in db1; a db2 tenant's reads never see db1's rows in
either direction; each shard's control loop checkpoints independently and the two position spaces do
not interfere; projection state and dead letters land shard-local; stopping one shard's loop leaves
the other consuming; a shard that is unreachable at startup degrades and is picked up when it
returns.

- [ ] Write the fixture.
- [ ] Write the tests.
- [ ] Run `dotnet test --filter "FullyQualifiedName~Sharded|FullyQualifiedName~UnshardedRegistrationParity"`.
- [ ] Commit: `test: cover tenants split across two databases`.

---

### Task 13: Documentation

**Files:**
- Create: `docs/architecture/tenant-sharding.md`
- Modify: `docs/multi-tenancy.md`, `docs/configuration.md`, `docs/operations.md`, `CLAUDE.md`

Cover the configuration surface, the pool-size multiplication (settings are per shard), the
degradation behaviour, the CLI's fan-out and mutation guard, and the two documented limits: no
tenant relocation, and no cross-shard reads — including what that means for the Orders example's
`getOrdersOverview`, and its interaction with the existing `tenantId` read/write mismatch, which
this work neither causes nor cures.

- [ ] Write the docs.
- [ ] Run `dotnet build` and the full `dotnet test`.
- [ ] Commit: `docs: document tenant sharding`.

---

## Final verification

- [ ] `dotnet build` (whole solution, including the CLI, which CI does not build).
- [ ] `dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`.
- [ ] Open the PR; merge when CI is green.
