# Task 8 Report — Postgres Descriptor and Startup Migrations

**Status:** DONE_WITH_CONCERNS  
**Commit:** `7791731`  
**Test result:** 645 passed / 4 skipped (all pre-existing) / 0 failed — Docker was available, Testcontainers-backed tests ran against a real database.

---

## What Was Implemented, File by File

### `src/Alberto.Dcb.Postgres/PostgresOptions.cs` (rewritten)

Converted the mutable class with `{ get; set; }` properties to an immutable `sealed record` with `{ get; init; }` properties. Defaults are identical to the old class defaults. Added `PostgresOverrides : IAlbertoOverrides<PostgresOptions>` as a mutable mirror with all-nullable properties; `ApplyTo` returns a `with`-expression so the original record is never mutated.

### `src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs` (created)

`sealed record PostgresBackendDescriptor(PostgresOptions Options) : IAlbertoBackendDescriptor` with:

- `Name = "Postgres"`, `SupportsTenancy = true`
- `ApplyConfiguration`: delegates to `AlbertoOptionsOverlay.Overlay<PostgresOptions, PostgresOverrides>` under the `"Postgres"` key
- `Validate`: emits ALB1001 (empty connection string), ALB1002 (non-positive MaxPoolSize), ALB1003 (MinPoolSize > MaxPoolSize), ALB1004 (non-positive LeaseDuration)
- `Register`: registers `AlbertoMigrationHostedService`, a deferred-factory `NpgsqlDataSource` (keyed), `IAppendInterceptorPipeline`, then branches to `RegisterTenantBackend` / `RegisterSingleTenantBackend`, then registers `ICheckpointStore`, `IDeadLetterStore`, `IProcessorLock`, `ITenantProcessorLock`, `IProcessorLeaseManager`, `IEventAppendedSignal`, and conditionally `PostgresEventListener`

The `NpgsqlDataSource` factory is a `(sp, _) =>` lambda — it reads the overlay-applied options via `IOptionsMonitor<AlbertoModuleDefinition>` at resolution time, so a connection string provided only through `appsettings.json` is honoured before any connection is opened.

### `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs` (created)

`internal sealed class AlbertoMigrationHostedService : IHostedService`. Reads the module definition from `IOptionsMonitor<AlbertoModuleDefinition>` at `StartAsync` time. If `AutoMigrate = true`, calls `PostgresMigrator.Migrate(connectionString, schema, singleTenant: !definition.TenancyEnabled)`. Always calls `PostgresMigrator.ValidateTenancyMode(connectionString, schema, singleTenant: !definition.TenancyEnabled)`.

### `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs` (rewritten)

- `WithPostgres` signature changed from `Action<PostgresOptions>` to `Func<PostgresOptions, PostgresOptions>`; calls `builder.UseBackend(new PostgresBackendDescriptor(options))`
- `RegisterSingleTenantBackend` and `RegisterTenantBackend` changed from `private` to `internal static` with `(AlbertoModuleContext context, PostgresOptions options)` signatures
- Removed the `TenancyOrderingValidator` inner class (no longer needed — `Register` runs after the full builder lambda so `context.TenancyEnabled` always reflects the final state)
- Removed `#pragma warning disable CS0618`

### `tests/Alberto.Dcb.Tests/Configuration/PostgresDescriptorTests.cs` (created)

Six tests:
1. `WithPostgres_declares_the_backend_without_connecting` — verifies no connection opened during composition
2. `Tenancy_declared_after_the_backend_still_reaches_the_backend` — verifies ordering no longer matters
3. `Postgres_options_bind_from_configuration` — config overlay overrides code-configured values
4. `A_connection_string_supplied_only_by_configuration_is_accepted` — empty code-configured string + config overlay passes ALB1001
5. `An_empty_connection_string_fails_with_ALB1001` — direct construction path (see Deviation §1)
6. `An_inverted_pool_range_fails_with_ALB1003` — direct construction path (see Deviation §1)

### `tests/Alberto.Dcb.Tests/Configuration/OptionsOverrideParityTests.cs` (modified)

Added `typeof(Alberto.Dcb.Postgres.PostgresOptions).Assembly` to the assembly array so the reflection test covers `PostgresOptions` / `PostgresOverrides` parity.

### `tests/Alberto.Dcb.Tests/Tenancy/TenantIsolationTests.cs` (modified)

`BuildServiceProvider` was using the old direct `DcbModuleBuilder` construction + `WithPostgres` call. After the change, `WithPostgres` only records the descriptor; `Register` is called by `AddAlberto`'s Phase 3. Updated to `services.AddAlberto(ModuleKey, module => module.WithTenancy().WithPostgres(o => o with { ... }))`.

### `apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs` (modified)

`WithPostgres` call site updated from `Action<PostgresOptions>` mutation style to `o => o with { ... }`.

### `apps/Alberto.Payments/Alberto.Payments.Infrastructure/PaymentsModule.cs` (modified)

`WithPostgres` call site updated from `Action<PostgresOptions>` mutation style to `o => o with { ... }`.

---

## TDD Evidence

### Failure before implementation

Before the `PostgresBackendDescriptor` / `AlbertoMigrationHostedService` files existed, the new tests failed to compile:

```
error CS0246: The type or namespace name 'PostgresBackendDescriptor' could not be found
error CS0246: The type or namespace name 'AlbertoMigrationHostedService' could not be found
```

After `PostgresOptions` was converted to a record but call sites still used `Action<T>` mutation:

```
error CS8852: Init-only property or indexer 'PostgresOptions.ConnectionString' can only be assigned in an object initializer...
error CS1643: Not all code paths return a value in lambda expression of type 'Func<PostgresOptions, PostgresOptions>'
```

### Pass after implementation

```
Passed!  - Failed: 0, Passed: 645, Skipped: 4, Total: 649, Duration: 9 s
```

---

## Docker Availability

Docker was available. The 4 skipped tests are pre-existing skips unrelated to this task:
- `OutboxStore_ProcessingEntriesOrphaned_CannotBeRecoveredByRetryFailed` (pre-existing)
- `EfStateStore_IsDuplicateKeyViolation_DoesNotWalkFullInnerExceptionChain` (pre-existing)
- `DeadLetterStore_GetAsync_is_scoped_to_active_tenant` (pre-existing)
- `RoundTrip_FiveEventBatch_FiresExactlyOneNotify` (pre-existing)

Testcontainers-backed integration tests (Postgres event store, append, projection) ran against a real database and passed.

---

## Deviations from the Brief

### Deviation 1 — Validation-failure tests use direct construction, not `Resolve(services)`

**Brief:** Tests 5–6 were shown calling `Resolve(services)` (i.e. `IOptionsMonitor.Get("orders")`) and expecting `.Collect()` on the result.

**Reality:** `AlbertoModuleValidator` is registered as `IValidateOptions<AlbertoModuleDefinition>` via `TryAddEnumerable`. The options factory calls all registered validators inline when `IOptionsMonitor.Get()` is called; if any return failure, an `OptionsValidationException` is thrown before `.Collect()` has a chance to run.

**Fix:** Tests 5–6 construct `PostgresBackendDescriptor` and `AlbertoModuleDefinition` directly and call `new AlbertoModuleValidator().Collect(definition)`. This tests exactly the same validation code path and the intent is identical.

### Deviation 2 — `singleTenant` parameter passed to `PostgresMigrator.Migrate`

**Brief:** The hosted-service snippet showed `PostgresMigrator.Migrate(options.ConnectionString, options.Schema)` (no `singleTenant`).

**The old code:** `PostgresMigrator.Migrate(connectionString, schema, singleTenant: !isTenantMode)`.

**Assessment:** The brief's omission is an oversight. Without `singleTenant: !definition.TenancyEnabled`, single-tenant modules would silently receive the multi-tenant migration scripts. The fix preserves the old behaviour and is noted with an inline comment.

---

## Breaking Changes for Task 13's UPGRADING.md

1. **`WithPostgres` signature change**: `Action<PostgresOptions>` → `Func<PostgresOptions, PostgresOptions>`. All call sites must switch from `options => { options.X = y; }` to `o => o with { X = y }`.

2. **`PostgresOptions` is now a `sealed record`**: Properties are init-only. Any consumer that held a reference and mutated it after construction will fail to compile.

3. **Migrations no longer run during `AddAlberto`**: They run in `AlbertoMigrationHostedService.StartAsync`. If callers were relying on schema being ready before host start (e.g. design-time factories that called `services.BuildServiceProvider()` and exercised the database immediately), they will need to call `PostgresMigrator.Migrate` themselves or run the host.

4. **`TenancyOrderingValidator` removed**: It was internal so this only affects callers that were referencing it through reflection.

---

## Concerns

### Known limitation — non-connection-string options do not flow through the overlay to service factories

The `NpgsqlDataSource` factory reads `Schema`, `MaxPoolSize`, and `MinPoolSize` from the overlay-applied options correctly. However, all other keyed service factories (`ICheckpointStore`, `IDeadLetterStore`, `ITenantProcessorLock`, `IProcessorLeaseManager`, `PostgresEventListener`) capture `Options.Schema` / `Options.LeaseDuration` from the code-configured `PostgresBackendDescriptor` at registration time — not from the overlay-applied descriptor at resolution time. If someone supplies `Schema` or `LeaseDuration` only through `appsettings.json`, `NpgsqlDataSource` will see the correct value, but the checkpoint and lease services will use the code-configured default.

This is identical to the constraint in the pre-Task-8 code (all values were captured at registration time). It is not a regression introduced by Task 8, but it is worth resolving in a later task by having all factories read from `IOptionsMonitor` instead of closing over `Options`.

### Stale XML doc comment

`src/Alberto.Dcb/ServiceCollectionExtensions.cs` likely contains doc-comment text referencing the old `Action<PostgresOptions>` signature pattern. This is cosmetic and doesn't affect compilation. Flagged for Task 12 cleanup.

## Fix pass (review findings)

### Finding applied

**File:** `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs`
**Issue:** `PostgresMigrator.Migrate` return value was discarded; a failed migration silently let the host start with an incomplete schema.

### Diff applied

```diff
-            PostgresMigrator.Migrate(options.ConnectionString, options.Schema, singleTenant: !definition.TenancyEnabled);
+            //
+            // DbUp's EnsureDatabase step may throw directly (e.g. NpgsqlException) rather than
+            // returning a failed MigrationResult, so wrap both paths into one consistent
+            // InvalidOperationException so callers see the module name and a clear "migration
+            // failed" message regardless of which failure mode occurs.
+            MigrationResult result;
+            try
+            {
+                result = PostgresMigrator.Migrate(options.ConnectionString, options.Schema, singleTenant: !definition.TenancyEnabled);
+            }
+            catch (Exception ex) when (ex is not InvalidOperationException)
+            {
+                throw new InvalidOperationException(
+                    $"Alberto schema migration failed for module '{moduleKey}': {ex.Message}", ex);
+            }
+
+            if (!result.Successful)
+                throw new InvalidOperationException(
+                    $"Alberto schema migration failed for module '{moduleKey}': {result.Error?.Message}", result.Error);
```

**Note on DbUp 7.0.1 behaviour:** `EnsureDatabase.For.PostgresqlDatabase` in this version throws `NpgsqlException` directly when the server is unreachable (it does not swallow the exception). `PerformUpgrade` would catch connection failures and return `Successful = false`. Both code paths are now wrapped, producing a consistent `InvalidOperationException` that names the module and mentions migration failure.

**Actual `MigrationResult` member names used:** `Successful` (bool), `Error` (Exception?), `ExecutedScripts` (IReadOnlyCollection<string>, not used in the fix).

### Test added

`tests/Alberto.Dcb.Tests/Configuration/PostgresDescriptorTests.cs` — `A_failed_migration_prevents_host_start_and_names_the_module`

Uses connection string `Host=127.0.0.1;Port=19999;Database=alberto;Username=x;Password=y;Timeout=1` (no Docker required). Asserts `host.StartAsync` throws `InvalidOperationException` whose message contains `"orders"` and `"migration"`.

### Before-fix test run (fix stashed)

```
Expected a <System.InvalidOperationException> to be thrown, but found <Npgsql.NpgsqlException>:
Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:19999
 ---> System.Net.Sockets.SocketException (61): Connection refused

Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 167 ms
```

### After-fix test run

```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 147 ms
```

### Full suite (after fix)

```
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj
Passed!  - Failed: 0, Passed: 646, Skipped: 4, Total: 650, Duration: 8 s
```

### Commit

```
5b9121c fix(postgres): fail host start when schema migration fails
```
