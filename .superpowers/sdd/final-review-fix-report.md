# Final Merge Review — Fix Report

Branch: `claude/alberto-release-dx-dee517`  
Date: 2026-07-25  
Reviewer baseline: 666 passed / 4 skipped / 0 failed  
Final result: **687 passed / 4 skipped / 0 failed** (21 new tests added, 0 weakened, 0 deleted)

---

## Commits

| Finding | Commit | Subject |
|---------|--------|---------|
| I3 | `1d2cfd3` | fix(telemetry): retain AddAlbertoInstrumentation for manual TracerProvider wiring |
| I1 | `512018d` | feat(config): per-processor execution options overlay from configuration |
| I2 | `4c595e2` | fix(postgres): resolve PostgresOptions from IOptionsMonitor inside factory lambdas |
| I4 | `f0cea3e` | feat(config): detect and report unknown configuration keys as ALB0008 |
| cleanup | `e020ca6` | test(telemetry): remove dead CS0618 pragma after [Obsolete] removal |

---

## Finding I3 — `AddAlbertoInstrumentation` must not carry `[Obsolete]`

**Commit:** `1d2cfd3bf8c9a5cebafe19d06b1d416d224df1cb`

### Problem

Both overloads in `src/Alberto.Dcb.Telemetry/ServiceCollectionExtensions.cs` were decorated with `[Obsolete]`. The spec retains them for users who wire `TracerProvider` / `MeterProvider` manually without calling `AddOpenTelemetry()`. The attribute was added by mistake in a prior review cycle.

### Red test (before)

Reflection-based assertions in `TelemetryRegistrationTests`:

```csharp
typeof(Alberto.Dcb.Telemetry.ServiceCollectionExtensions)
    .GetMethods()
    .Where(m => m.Name == "AddAlbertoInstrumentation" && ...)
    .Should().ContainSingle()
    .Which.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
    .Should().BeEmpty(...);
```

Both `_TracerProviderBuilder_is_not_obsolete` and `_MeterProviderBuilder_is_not_obsolete` failed before the fix because `ObsoleteAttribute` was present.

> Note: `TreatWarningsAsErrors` is on in source projects but **not** in test projects, so calling an `[Obsolete]`-marked method from tests produces a warning only — not a compile error. Reflection was the only reliable red gate.

### Changes

**`src/Alberto.Dcb.Telemetry/ServiceCollectionExtensions.cs`**

- Removed `[Obsolete]` from both overloads (`TracerProviderBuilder` and `MeterProviderBuilder`).
- Updated XML docs to explain the retained use case: manual TracerProvider / MeterProvider wiring in hosts that do not call `AddOpenTelemetry()`.
- Added `ArgumentNullException.ThrowIfNull(builder)` to both methods (was missing and would have been a latent NPE).

**`tests/Alberto.Dcb.Tests/Configuration/TelemetryRegistrationTests.cs`**

Added three tests under "Finding I3":

| Test | What it checks |
|------|----------------|
| `AddAlbertoInstrumentation_TracerProviderBuilder_is_not_obsolete` | `ObsoleteAttribute` absent via reflection |
| `AddAlbertoInstrumentation_MeterProviderBuilder_is_not_obsolete` | Same for the `MeterProviderBuilder` overload |
| `AddAlbertoInstrumentation_is_idempotent_with_WithTelemetry` | Calling both `WithTelemetry()` and `AddAlbertoInstrumentation()` does not double-register the source — exactly one activity exported |

**`UPGRADING.md`**

Updated the I3 row from "`AddAlbertoInstrumentation()` is `[Obsolete]`" to "`AddAlbertoInstrumentation()` retained for manual `TracerProvider`/`MeterProvider` wiring".

### Cleanup commit `e020ca6`

After removing `[Obsolete]`, the `#pragma warning disable CS0618` / `restore CS0618` wrapping `AddAlbertoInstrumentation()` in the idempotency test became dead code. Removed in a follow-up commit.

---

## Finding I1 — Per-processor execution options overlay from configuration

**Commit:** `512018d3b968186e7f532b51f7bd83f96e49cf23`

### Problem

`AlbertoModuleDefinition.ApplyConfiguration` overlaid `ControlLoopOptions`, `TelemetryOptions`, and `CheckpointOptions` from configuration, but had no mechanism for per-processor execution settings under `Alberto:Modules:{key}:Processors:{processorId}`. Additionally, the ALB0005 validator only fired for code-declared conflicts; a config-only combination (`MaxConcurrency > 1`, `BatchingMode = Disabled`) was silently ignored.

### Red tests (before)

`ProcessorExecutionConfigurationTests` (new file):

```
Config_MaxConcurrency_overrides_the_target_processor              FAIL
Config_BatchingMode_overrides_the_target_processor                FAIL
Config_section_for_unknown_processor_does_not_throw               FAIL
Absent_processor_config_leaves_defaults_intact                    FAIL
Config_only_ALB0005_violation_is_detected                         FAIL
```

All five failed (no overlay path existed).

### Changes

**`src/Alberto.Dcb/Subscriptions/ProcessorExecutionOptions.cs`**

Added `ProcessorExecutionOverrides` — the mirror class for `ProcessorExecutionOptions`:

```csharp
public sealed class ProcessorExecutionOverrides : IAlbertoOverrides<ProcessorExecutionOptions>
{
    public ProcessorBatchingMode? BatchingMode { get; set; }
    public int? MaxConcurrency { get; set; }
    public ProcessorExecutionOptions ApplyTo(ProcessorExecutionOptions options) =>
        options with
        {
            BatchingMode = BatchingMode ?? options.BatchingMode,
            MaxConcurrency = MaxConcurrency ?? options.MaxConcurrency,
        };
}
```

This is automatically picked up by the reflection-driven `OptionsOverrideParityTests` guard — no extra wiring needed there.

**`src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`**

In `ApplyConfiguration`, added a processor overlay loop before the `return definition with { ... }`:

```csharp
var processorsSection = section.GetSection("Processors");
var overlaidProcessors = definition.Processors.Select(processor =>
{
    var procSection = processorsSection.GetSection(processor.ProcessorId);
    if (!procSection.Exists()) return processor;
    return processor with
    {
        Execution = AlbertoOptionsOverlay.Overlay<ProcessorExecutionOptions, ProcessorExecutionOverrides>(
            processorsSection, processor.ProcessorId, processor.Execution),
    };
}).ToImmutableArray();
```

The `Processors = overlaidProcessors` expression is included in the returned `with` record. ALB0005 validation already runs against all processors in `AlbertoModuleValidator.ValidateProcessors`, so config-only violations are caught automatically.

**`tests/Alberto.Dcb.Tests/Configuration/ProcessorExecutionConfigurationTests.cs`** (new file)

5 tests verifying: target-only overlay, adjacent processors unaffected, unknown processor IDs do not throw, absent config leaves defaults, config-only ALB0005 fires.

---

## Finding I2 — Stale option captures in Postgres factory lambdas

**Commit:** `4c595e2a5803c557d1efdc15e0b269a68bb93091`

### Problem

Five factory lambdas in `PostgresBackendDescriptor` and one in `PostgresBuilderExtensions` captured the `Options` / `fallback` values at **composition time** — i.e., before the named options pipeline had a chance to apply configuration overlays. This meant any value set via `Alberto:Modules:{key}:Postgres:*` was ignored at runtime by those factories.

The affected services were: `IEventStoreBackend` (single-tenant and multi-tenant), `IProcessorLeaseManager`, `ICheckpointStore`, `IHostedService` (NOTIFY listener), and the bulk of `PostgresTenantEventStoreBackend`.

### Red test (before)

`PostgresDescriptorTests.Config_supplied_LeaseDuration_reaches_IProcessorLeaseManager` — resolved the keyed `IProcessorLeaseManager` and asserted `LeaseDuration == 3min` when config specified `Postgres:LeaseDuration = 00:03:00`. Failed because the factory captured the code-declared default (`30s`) at composition time.

The test avoids Docker by building a service provider with a fake connection string — `PostgresProcessorLeaseManager` exposes a public `LeaseDuration` property and doesn't connect to the DB in its constructor, so the assertion is pure DI without a live DB.

### Changes

**`src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs`**

Each of the five factory lambdas was changed to resolve from `IOptionsMonitor<AlbertoModuleDefinition>` at resolution time:

```csharp
// Before (stale capture):
services.AddKeyedSingleton<IProcessorLeaseManager>(moduleKey, (sp, _) =>
    new PostgresProcessorLeaseManager(/* ... */, Options.LeaseDuration));

// After (deferred resolution):
services.AddKeyedSingleton<IProcessorLeaseManager>(moduleKey, (sp, _) =>
{
    var definition = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey);
    var opts = definition.Backend is PostgresBackendDescriptor desc ? desc.Options : Options;
    return new PostgresProcessorLeaseManager(/* ... */, opts.LeaseDuration);
});
```

The `EnableNotifyListener` conditional previously short-circuited `AddSingleton<IHostedService>` at composition time:

```csharp
// Before (composition-time branch — missed config):
if (Options.EnableNotifyListener)
    services.AddSingleton<IHostedService>(...);

// After (always register, check at resolution):
services.AddSingleton<IHostedService>(sp =>
{
    var opts = /* resolve from monitor */;
    if (!opts.EnableNotifyListener) return PostgresNullHostedService.Instance;
    return new PostgresListenNotifyService(...);
});
```

`PostgresNullHostedService` is a `file sealed class` — file-local, zero-allocation no-op implementation of `IHostedService`.

**`src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs`**

Same deferred-resolution pattern applied to the `IEventStoreBackend` factory (single-tenant path via `RegisterSingleTenantBackend`) and the `PostgresTenantEventStoreBackend` factory.

---

## Finding I4 — Unknown configuration keys reported as ALB0008

**Commit:** `f0cea3e75c8cad0b4d8a0a25395b80eef580aeb6`

### Problem

Typos in configuration keys under `Alberto:Modules:{key}` were silently ignored. There was no mechanism to detect or report them. A user who wrote `ControlLoop:PoolingInterval` instead of `PollingInterval` would get no warning, and the default value would be silently used.

### Red tests (before)

`UnknownConfigurationKeyTests` (new file, 12 tests):

```
Valid_keys_produce_no_ALB0008                                     FAIL
Unknown_top_level_section_produces_ALB0008                        FAIL
Typo_in_ControlLoop_leaf_key_produces_ALB0008                     FAIL
Typo_in_leaf_key_produces_did_you_mean_suggestion                 FAIL
Exact_match_is_not_flagged                                         FAIL
... (all 12)
```

All failed because `UnknownConfigurationKeys` did not exist and scanning was not implemented.

### New types

**`src/Alberto.Dcb/Configuration/UnknownConfigurationKey.cs`** (public API)

```csharp
public sealed record UnknownConfigurationKey(string FullKey, string? Suggestion);
```

**`src/Alberto.Dcb/Configuration/AlbertoConfigurationScanner.cs`** (internal)

Static class with:

- `Scan(IConfigurationSection moduleSection, IAlbertoBackendDescriptor? backend)` — entry point; returns `ImmutableArray<UnknownConfigurationKey>`.
- `ScanSection(IConfigurationSection section, Type overridesType, List<UnknownConfigurationKey> findings)` — recursive; walks all children of a section and flags any key whose name does not match a property of the `IAlbertoOverrides<TOptions>` type. Recurses when a matched property's underlying type also implements `IAlbertoOverrides<>`.
- `FindBestMatch(string key, IEnumerable<string> candidates)` — Levenshtein with threshold `Math.Max(2, candidate.Length / 3)` (OrdinalIgnoreCase).
- `LevenshteinDistance` — standard two-row O(m·n) implementation.

Top-level section routing:

| Key | Handler |
|-----|---------|
| `ControlLoop` | `typeof(ControlLoopOverrides)` |
| `Telemetry` | `typeof(TelemetryOverrides)` |
| `Checkpoints` | `typeof(CheckpointOverrides)` |
| `Processors` | `null` (special — children are dynamic processor IDs, not flagged; each processor ID's children are validated against `ProcessorExecutionOverrides`) |
| Backend key (e.g. `Postgres`) | returned by `IAlbertoBackendDescriptor.GetConfigurationSection()` |
| Any other key | flagged as ALB0008 immediately |

**`src/Alberto.Dcb/Configuration/IAlbertoBackendDescriptor.cs`**

Added a default interface method to avoid a circular dependency between the core library and backend libraries:

```csharp
(string? SectionName, Type? OverridesType) GetConfigurationSection() => (null, null);
```

**`src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs`**

Override:

```csharp
public (string? SectionName, Type? OverridesType) GetConfigurationSection() =>
    ("Postgres", typeof(PostgresOverrides));
```

**`src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`**

Added `UnknownConfigurationKeys` property and wired the scanner call into `ApplyConfiguration`:

```csharp
public ImmutableArray<UnknownConfigurationKey> UnknownConfigurationKeys { get; internal set; } = [];
```

**`src/Alberto.Dcb/ServiceCollectionExtensions.cs`**

Added `target.UnknownConfigurationKeys = source.UnknownConfigurationKeys;` to `CopyInto`.

**`src/Alberto.Dcb/Configuration/AlbertoModuleValidator.cs`**

Added `ValidateUnknownKeys` (called from `Collect`):

```csharp
private static void ValidateUnknownKeys(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
{
    foreach (var key in definition.UnknownConfigurationKeys)
    {
        var remedy = key.Suggestion is not null
            ? $"Did you mean '{key.Suggestion}'? Correct or remove this key."
            : "This key is not recognised. Correct or remove it.";
        failures.Add(new AlbertoValidationFailure("ALB0008",
            $"Unknown configuration key '{key.FullKey}'.", remedy));
    }
}
```

**`docs/configuration.md`**

Added `ALB0008` row to the validation catalog.

**`UPGRADING.md`**

Added: "Unknown keys under `Alberto:Modules:{key}` now fail startup with `ALB0008`."

---

## Judgment calls

| Area | Decision | Rationale |
|------|----------|-----------|
| I3 test gate | Reflection assertions (`ObsoleteAttribute` absent), not compile-failure | Test project does not have `TreatWarningsAsErrors`, so calling an `[Obsolete]` method only warns. Reflection is the only reliable red gate. |
| I2 no-DB test | Resolve keyed `IProcessorLeaseManager` with fake connection string; assert `.LeaseDuration` | `PostgresProcessorLeaseManager` exposes `LeaseDuration` as a public property and does not connect in its constructor. Avoids Testcontainers for a pure DI assertion. |
| I4 backend section | Default interface method `GetConfigurationSection()` on `IAlbertoBackendDescriptor` | Keeps the unknown-key scanner in the core `Alberto.Dcb` assembly without importing `Alberto.Dcb.Postgres` (which would be a circular dependency). Each backend self-describes its section name. |
| I4 Processors handling | Map `"Processors" → null`; iterate processor-ID children, validate each against `ProcessorExecutionOverrides` | Processor IDs are user-defined at runtime. Flagging them as unknown keys would be a false positive. Only their leaf property names (e.g. `MaxConcurrency`, `BatchingMode`) are validated. |
| I4 Levenshtein threshold | `Math.Max(2, candidate.Length / 3)` (OrdinalIgnoreCase) | Specified in the review findings. Keeps short keys (≤ 5 chars) requiring at most 2 edits; longer keys scale at 33% of their length. |
| I1 ALB0005 config-only coverage | No extra validator code needed | `ValidateProcessors` already iterates `definition.Processors`. After the overlay loop in `ApplyConfiguration`, config-supplied values are present in the processor records, so the existing validator catches them automatically. |
| `PostgresNullHostedService` | `file sealed class` (file-local type) | Consistent with project conventions (same pattern seen in `PostgresBackendDescriptor`). Zero-allocation no-op; never needs to be visible outside the file. |

---

## Build output (final)

```
dotnet build — source projects
    0 Warning(s)
    0 Error(s)

dotnet test
    Passed!  - Failed: 0, Passed: 687, Skipped: 4, Total: 691
```

Baseline: 666 / 4 / 0. All 4 skips are unchanged pre-existing skips.
