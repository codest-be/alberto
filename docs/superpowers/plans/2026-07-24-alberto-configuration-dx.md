# Alberto Configuration API & Startup Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Alberto's three competing configuration idioms with a single declarative options-record spine that binds to `IConfiguration`, is order-independent, does no I/O during DI composition, and reports every misconfiguration at startup with an actionable message.

**Architecture:** Three phases. **Phase 1 (declare)** — `AddAlberto` runs the user's lambda against a `DcbModuleBuilder` that accumulates an immutable `AlbertoModuleDefinition` plus a list of deferred registration callbacks; it touches no `IServiceCollection` and opens no connections. **Phase 2 (bind + validate)** — the definition is registered as a *named* options instance (`AddOptions<AlbertoModuleDefinition>(moduleKey)`), overlaid from `Alberto:Modules:{moduleKey}:*`, and checked by `IValidateOptions<AlbertoModuleDefinition>` under `ValidateOnStart()`. **Phase 3 (run)** — deferred callbacks register services; migrations and other I/O move into hosted services that run at startup.

**Tech Stack:** .NET 10 / C# 14, `Microsoft.Extensions.Options` + `Microsoft.Extensions.Configuration.Binder`, xUnit v3, FluentAssertions, Testcontainers.PostgreSql, OpenTelemetry 1.15.3 (`OpenTelemetry.Extensions.Hosting`).

**Spec:** [docs/superpowers/specs/2026-07-24-alberto-configuration-dx-design.md](../specs/2026-07-24-alberto-configuration-dx-design.md)

## Global Constraints

- Source projects multi-target `net9.0;net10.0`; test projects target `net10.0`. Do not change target frameworks.
- `TreatWarningsAsErrors` is on (`Directory.Build.props`). A build warning fails the build. Nullable is enabled everywhere.
- NuGet versions are centralized in `Directory.Packages.props`. `PackageReference` elements in `.csproj` files carry **no** `Version` attribute. Adding a new package means adding a `PackageVersion` entry first.
- `OpenTelemetry.Extensions.Hosting` is already pinned at `1.15.3` in `Directory.Packages.props`.
- Tests use xUnit v3 (`[Fact]`, `[Theory]`) with FluentAssertions (`.Should()`). Do not add a different assertion library.
- This is the 1.0 reset: **breaking changes are allowed and expected.** Every breaking change must land a row in `UPGRADING.md` (Task 13).
- Public API additions get XML doc comments — the projects are packable and `GenerateDocumentationFile` is on.
- Every configuration knob has exactly one home: an options record property. No knob may exist only as a builder method.
- Configuration keys are always `Alberto:Modules:{moduleKey}:{Section}:{Property}`.
- Solution file is `AlbertoV3.slnx`. Build with `dotnet build`, test with `dotnet test`.

## File Structure

**New — `src/Alberto.Dcb/Configuration/`** (the options spine)

| File | Responsibility |
|---|---|
| `IAlbertoOverrides.cs` | `IAlbertoOverrides<TOptions>` marker + `AlbertoOptionsOverlay.Overlay(...)` helper |
| `ControlLoopOptions.cs` | `ControlLoopOptions` record + `ControlLoopOverrides` mirror |
| `RetryOptions.cs` | `RetryOptions`, `DeadLetterRetryOptions`, `ProcessorLeaseOptions` records + mirrors |
| `TelemetryOptions.cs` | `TelemetryOptions` record + mirror |
| `CheckpointOptions.cs` | `CheckpointOptions` record, `OrphanCheckpointPolicy` enum + mirror |
| `AlbertoModuleDefinition.cs` | The accumulated, immutable module declaration |
| `AlbertoModuleContext.cs` | What deferred registration callbacks receive |
| `IAlbertoBackendDescriptor.cs` | Backend extension point that replaces `builder.Services` |
| `AlbertoValidationFailure.cs` | Failure record + message formatter |
| `AlbertoModuleValidator.cs` | `IValidateOptions<AlbertoModuleDefinition>` |
| `ProcessorIdAttribute.cs` | `[ProcessorId("...")]` + `ProcessorId.For<T>()` derivation |

**Modified — `src/Alberto.Dcb/`**

| File | Change |
|---|---|
| `DcbModuleBuilder.cs` | Accumulates a definition + deferred callbacks; `Services` removed in Task 12 |
| `ServiceCollectionExtensions.cs` | Wires named options, config overlay, validation, deferred registration |
| `DcbModuleBuilderExtensions.cs` | `WithControlLoop`/`ReactTo`/`AddConsumeMiddleware` signature changes |
| `ControlLoopBuilder.cs` | **Deleted** (Task 6); replaced by `ControlLoopOptions` |
| `Subscriptions/ErrorPolicy.cs` | **Deleted** (Task 6); split into `RetryOptions` + declared `IErrorClassifier` |
| `Subscriptions/ProcessorExecutionOptions.cs` | `ProcessorExecutionConfigurator` deleted (Task 7) |
| `Subscriptions/ICheckpointInventory.cs` | **New** — optional enumeration capability for orphan detection |

**Modified — backends**

| File | Change |
|---|---|
| `src/Alberto.Dcb.Postgres/PostgresOptions.cs` | class → record + `PostgresOverrides` mirror |
| `src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs` | **New** — implements `IAlbertoBackendDescriptor` |
| `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs` | Deferred registration; `TenancyOrderingValidator` deleted; migrations → hosted service |
| `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs` | **New** — DbUp + tenancy-mode check at startup, not at composition |
| `src/Alberto.Dcb.InMemory/InMemoryBackendDescriptor.cs` | **New** |
| `src/Alberto.Dcb.EntityFramework/EfBuilderExtensions.cs` | Deferred registration (keeps EF's own `Action<DbContextOptionsBuilder>` idiom) |
| `src/Alberto.Dcb.Telemetry/TelemetryBuilderExtensions.cs` | Self-registers OTel sources/meters |

**Tests — `tests/Alberto.Dcb.Tests/Configuration/`** (new directory)

`OptionsOverrideParityTests.cs`, `OptionsDefaultsTests.cs`, `ConfigurationOverlayTests.cs`, `AlbertoModuleValidatorTests.cs`, `ValidationMessageTests.cs`, `ProcessorIdTests.cs`, `ModuleDefinitionTests.cs`, `StartupValidationTests.cs`, `OrphanCheckpointTests.cs`.

---

## Task Sequencing Note

Tasks 1–7 are **additive**: they build the new spine alongside the old builder, and `DcbModuleBuilder.Services` keeps working the whole time, so the solution stays green. Tasks 8–11 migrate one backend/assembly at a time. Task 12 removes `Services` and migrates every call site. Task 13 documents.

Run `dotnet build` after every task. It must succeed with zero warnings.

---

### Task 1: Options records and their override mirrors

**Files:**
- Create: `src/Alberto.Dcb/Configuration/IAlbertoOverrides.cs`
- Create: `src/Alberto.Dcb/Configuration/RetryOptions.cs`
- Create: `src/Alberto.Dcb/Configuration/ControlLoopOptions.cs`
- Create: `src/Alberto.Dcb/Configuration/TelemetryOptions.cs`
- Create: `src/Alberto.Dcb/Configuration/CheckpointOptions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/OptionsOverrideParityTests.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/OptionsDefaultsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Alberto.Dcb.Configuration.IAlbertoOverrides<TOptions>` with `TOptions ApplyTo(TOptions options)`; records `ControlLoopOptions`, `RetryOptions`, `DeadLetterRetryOptions`, `ProcessorLeaseOptions`, `TelemetryOptions`, `CheckpointOptions`; enum `OrphanCheckpointPolicy { Off, Warn, Strict }`; mirrors `ControlLoopOverrides`, `RetryOverrides`, `DeadLetterRetryOverrides`, `ProcessorLeaseOverrides`, `TelemetryOverrides`, `CheckpointOverrides`; helper `AlbertoOptionsOverlay.Overlay<TOptions, TOverrides>(IConfiguration parent, string key, TOptions current)`.

- [ ] **Step 1: Write the failing parity test**

Create `tests/Alberto.Dcb.Tests/Configuration/OptionsOverrideParityTests.cs`:

```csharp
using System.Reflection;
using Alberto.Dcb.Configuration;
using FluentAssertions;

namespace Alberto.Dcb.Tests.Configuration;

/// <summary>
/// Guards the hand-written nullable mirror records against drift. Adding a property to an
/// options record without adding it to the mirror silently makes that knob unconfigurable
/// from appsettings.json; this test turns that into a build failure instead.
/// </summary>
public class OptionsOverrideParityTests
{
    private static IReadOnlyList<(Type Overrides, Type Options)> DiscoverPairs()
    {
        var assemblies = new[]
        {
            typeof(ControlLoopOptions).Assembly,
        };

        return assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAlbertoOverrides<>))
                .Select(i => (Overrides: t, Options: i.GetGenericArguments()[0])))
            .ToList();
    }

    private static PropertyInfo[] PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0)
            .Where(p => p.Name != "EqualityContract")
            .ToArray();

    private static bool IsValidMirror(Type optionType, Type mirrorType)
    {
        if (Nullable.GetUnderlyingType(mirrorType) == optionType)
            return true;

        if (!optionType.IsValueType && mirrorType == optionType)
            return true;

        return mirrorType.GetInterfaces().Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IAlbertoOverrides<>)
            && i.GetGenericArguments()[0] == optionType);
    }

    [Fact]
    public void At_least_one_options_override_pair_is_discovered()
    {
        DiscoverPairs().Should().NotBeEmpty(
            "the parity test is worthless if it silently discovers nothing");
    }

    [Fact]
    public void Every_options_property_has_a_settable_nullable_mirror()
    {
        var problems = new List<string>();

        foreach (var (overridesType, optionsType) in DiscoverPairs())
        {
            foreach (var prop in PublicProperties(optionsType))
            {
                var mirror = overridesType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);

                if (mirror is null)
                {
                    problems.Add($"{overridesType.Name} is missing '{prop.Name}'.");
                    continue;
                }

                if (mirror.SetMethod?.IsPublic != true)
                {
                    problems.Add($"{overridesType.Name}.{prop.Name} needs a public setter so the configuration binder can write it.");
                    continue;
                }

                if (!IsValidMirror(prop.PropertyType, mirror.PropertyType))
                {
                    problems.Add(
                        $"{overridesType.Name}.{prop.Name} is '{mirror.PropertyType.Name}' but should be a nullable " +
                        $"'{prop.PropertyType.Name}' or an IAlbertoOverrides<{prop.PropertyType.Name}>.");
                }
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Override_mirrors_have_no_properties_the_options_record_lacks()
    {
        var problems = new List<string>();

        foreach (var (overridesType, optionsType) in DiscoverPairs())
        {
            var known = PublicProperties(optionsType).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var mirror in PublicProperties(overridesType))
            {
                if (!known.Contains(mirror.Name))
                    problems.Add($"{overridesType.Name}.{mirror.Name} does not exist on {optionsType.Name}.");
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~OptionsOverrideParityTests"
```

Expected: compile error — `The type or namespace name 'Configuration' does not exist in the namespace 'Alberto.Dcb'`.

- [ ] **Step 3: Create the marker interface and overlay helper**

Create `src/Alberto.Dcb/Configuration/IAlbertoOverrides.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// A mutable, all-nullable mirror of an immutable options record. The configuration binder
/// writes into the mirror; <see cref="ApplyTo"/> folds the values that were actually present
/// onto the code-configured defaults.
/// </summary>
/// <typeparam name="TOptions">The immutable options record this type mirrors.</typeparam>
public interface IAlbertoOverrides<TOptions>
    where TOptions : class
{
    /// <summary>
    /// Returns <paramref name="options"/> with every non-null override applied.
    /// Null properties leave the corresponding option untouched.
    /// </summary>
    TOptions ApplyTo(TOptions options);
}

/// <summary>
/// Binds an <see cref="IAlbertoOverrides{TOptions}"/> mirror from a configuration section and
/// applies it. Backend packages use this to overlay their own options records.
/// </summary>
public static class AlbertoOptionsOverlay
{
    /// <summary>
    /// Reads <paramref name="key"/> from <paramref name="parent"/> and applies it to
    /// <paramref name="current"/>. Returns <paramref name="current"/> unchanged when the
    /// section is absent.
    /// </summary>
    public static TOptions Overlay<TOptions, TOverrides>(
        IConfiguration parent,
        string key,
        TOptions current)
        where TOptions : class
        where TOverrides : class, IAlbertoOverrides<TOptions>
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(current);

        var section = parent.GetSection(key);
        if (!section.Exists())
            return current;

        var overrides = section.Get<TOverrides>();
        return overrides is null ? current : overrides.ApplyTo(current);
    }
}
```

- [ ] **Step 4: Create the retry / dead-letter / lease options**

Create `src/Alberto.Dcb/Configuration/RetryOptions.cs`:

```csharp
namespace Alberto.Dcb.Configuration;

/// <summary>
/// Retry behaviour applied to a failing event handler before it is dead-lettered.
/// </summary>
public sealed record RetryOptions
{
    /// <summary>Maximum retry attempts before escalating. Default 3.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before the first retry. Default 1 second.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Exponential backoff multiplier. 1.0 means a constant delay. Default 2.0.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Upper bound for the backed-off delay. Default 30 seconds.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether exhausted events are dead-lettered (true) or skipped (false). Default true.</summary>
    public bool DeadLetterOnMaxRetries { get; init; } = true;

    /// <summary>Delay before attempt <paramref name="attemptNumber"/> (1-based), capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan CalculateDelay(int attemptNumber)
    {
        if (attemptNumber <= 1)
            return RetryDelay;

        var multiplier = Math.Pow(BackoffMultiplier, attemptNumber - 1);
        var delay = TimeSpan.FromMilliseconds(RetryDelay.TotalMilliseconds * multiplier);

        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }
}

/// <summary>Configuration mirror for <see cref="RetryOptions"/>.</summary>
public sealed class RetryOverrides : IAlbertoOverrides<RetryOptions>
{
    public int? MaxRetries { get; set; }
    public TimeSpan? RetryDelay { get; set; }
    public double? BackoffMultiplier { get; set; }
    public TimeSpan? MaxRetryDelay { get; set; }
    public bool? DeadLetterOnMaxRetries { get; set; }

    /// <inheritdoc />
    public RetryOptions ApplyTo(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            MaxRetries = MaxRetries ?? options.MaxRetries,
            RetryDelay = RetryDelay ?? options.RetryDelay,
            BackoffMultiplier = BackoffMultiplier ?? options.BackoffMultiplier,
            MaxRetryDelay = MaxRetryDelay ?? options.MaxRetryDelay,
            DeadLetterOnMaxRetries = DeadLetterOnMaxRetries ?? options.DeadLetterOnMaxRetries,
        };
    }
}

/// <summary>
/// Behaviour of the background loop that re-attempts dead-lettered events.
/// </summary>
public sealed record DeadLetterRetryOptions
{
    /// <summary>How often the retry loop polls for due dead letters. Default 1 minute.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Dead letters claimed per poll. Default 10.</summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>How long a claimed dead letter stays claimed. Default 15 minutes.</summary>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Configuration mirror for <see cref="DeadLetterRetryOptions"/>.</summary>
public sealed class DeadLetterRetryOverrides : IAlbertoOverrides<DeadLetterRetryOptions>
{
    public TimeSpan? PollingInterval { get; set; }
    public int? BatchSize { get; set; }
    public TimeSpan? ClaimLease { get; set; }

    /// <inheritdoc />
    public DeadLetterRetryOptions ApplyTo(DeadLetterRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            PollingInterval = PollingInterval ?? options.PollingInterval,
            BatchSize = BatchSize ?? options.BatchSize,
            ClaimLease = ClaimLease ?? options.ClaimLease,
        };
    }
}

/// <summary>
/// Single-writer processor leasing, used when more than one replica runs the same module.
/// </summary>
public sealed record ProcessorLeaseOptions
{
    /// <summary>Whether processors acquire a fenced lease before consuming. Default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>Stable identity for this replica. Defaults to the machine name when null.</summary>
    public string? ReplicaId { get; init; }
}

/// <summary>Configuration mirror for <see cref="ProcessorLeaseOptions"/>.</summary>
public sealed class ProcessorLeaseOverrides : IAlbertoOverrides<ProcessorLeaseOptions>
{
    public bool? Enabled { get; set; }
    public string? ReplicaId { get; set; }

    /// <inheritdoc />
    public ProcessorLeaseOptions ApplyTo(ProcessorLeaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            ReplicaId = ReplicaId ?? options.ReplicaId,
        };
    }
}
```

> **Note on `ProcessorLeaseOverrides.ReplicaId`:** the mirror type is `string?` and the option is `string?`, which `IsValidMirror` accepts via the reference-type branch. A configured empty string is a real value and wins over the code default; that is intentional.

- [ ] **Step 5: Create the control loop, telemetry and checkpoint options**

Create `src/Alberto.Dcb/Configuration/ControlLoopOptions.cs`:

```csharp
namespace Alberto.Dcb.Configuration;

/// <summary>
/// Everything that governs the async control loop for one Alberto module.
/// </summary>
public sealed record ControlLoopOptions
{
    /// <summary>How often the consumer polls for new events. Default 250 ms.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum events fetched per poll. Default 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>How often the stable-head tracker refreshes. Default 100 ms.</summary>
    public TimeSpan HeadRefreshInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Size of the in-flight transaction window the head tracker keeps. Default 2000.</summary>
    public int HeadWindowSize { get; init; } = 2000;

    /// <summary>Retry behaviour for failing handlers.</summary>
    public RetryOptions Retry { get; init; } = new();

    /// <summary>Behaviour of the dead-letter retry loop.</summary>
    public DeadLetterRetryOptions DeadLetterRetry { get; init; } = new();

    /// <summary>Single-writer leasing across replicas.</summary>
    public ProcessorLeaseOptions Leases { get; init; } = new();

    /// <summary>The all-defaults control loop.</summary>
    public static ControlLoopOptions Default { get; } = new();
}

/// <summary>Configuration mirror for <see cref="ControlLoopOptions"/>.</summary>
public sealed class ControlLoopOverrides : IAlbertoOverrides<ControlLoopOptions>
{
    public TimeSpan? PollingInterval { get; set; }
    public int? BatchSize { get; set; }
    public TimeSpan? HeadRefreshInterval { get; set; }
    public int? HeadWindowSize { get; set; }
    public RetryOverrides? Retry { get; set; }
    public DeadLetterRetryOverrides? DeadLetterRetry { get; set; }
    public ProcessorLeaseOverrides? Leases { get; set; }

    /// <inheritdoc />
    public ControlLoopOptions ApplyTo(ControlLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            PollingInterval = PollingInterval ?? options.PollingInterval,
            BatchSize = BatchSize ?? options.BatchSize,
            HeadRefreshInterval = HeadRefreshInterval ?? options.HeadRefreshInterval,
            HeadWindowSize = HeadWindowSize ?? options.HeadWindowSize,
            Retry = Retry?.ApplyTo(options.Retry) ?? options.Retry,
            DeadLetterRetry = DeadLetterRetry?.ApplyTo(options.DeadLetterRetry) ?? options.DeadLetterRetry,
            Leases = Leases?.ApplyTo(options.Leases) ?? options.Leases,
        };
    }
}
```

Create `src/Alberto.Dcb/Configuration/TelemetryOptions.cs`:

```csharp
namespace Alberto.Dcb.Configuration;

/// <summary>
/// Controls Alberto's OpenTelemetry instrumentation for one module.
/// </summary>
public sealed record TelemetryOptions
{
    /// <summary>Whether tracing and metrics instrumentation is active. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether append spans carry the serialized payload size. Default true.</summary>
    public bool RecordEventPayloadSize { get; init; } = true;
}

/// <summary>Configuration mirror for <see cref="TelemetryOptions"/>.</summary>
public sealed class TelemetryOverrides : IAlbertoOverrides<TelemetryOptions>
{
    public bool? Enabled { get; set; }
    public bool? RecordEventPayloadSize { get; set; }

    /// <inheritdoc />
    public TelemetryOptions ApplyTo(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            RecordEventPayloadSize = RecordEventPayloadSize ?? options.RecordEventPayloadSize,
        };
    }
}
```

Create `src/Alberto.Dcb/Configuration/CheckpointOptions.cs`:

```csharp
namespace Alberto.Dcb.Configuration;

/// <summary>
/// What to do about checkpoints in the store that no declared processor claims.
/// </summary>
public enum OrphanCheckpointPolicy
{
    /// <summary>Ignore orphaned checkpoints.</summary>
    Off = 0,

    /// <summary>Log a warning naming each orphan. The default in Development.</summary>
    Warn = 1,

    /// <summary>Fail startup. The default outside Development.</summary>
    Strict = 2,
}

/// <summary>
/// Checkpoint hygiene settings for one module.
/// </summary>
public sealed record CheckpointOptions
{
    /// <summary>
    /// How to react to checkpoints whose processor id no longer matches any declared processor —
    /// usually the fingerprint of a renamed handler silently restarting from position zero.
    /// </summary>
    public OrphanCheckpointPolicy OrphanPolicy { get; init; } = OrphanCheckpointPolicy.Warn;
}

/// <summary>Configuration mirror for <see cref="CheckpointOptions"/>.</summary>
public sealed class CheckpointOverrides : IAlbertoOverrides<CheckpointOptions>
{
    public OrphanCheckpointPolicy? OrphanPolicy { get; set; }

    /// <inheritdoc />
    public CheckpointOptions ApplyTo(CheckpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            OrphanPolicy = OrphanPolicy ?? options.OrphanPolicy,
        };
    }
}
```

- [ ] **Step 6: Add the `Microsoft.Extensions.Configuration.Binder` reference**

`AlbertoOptionsOverlay.Overlay` calls `section.Get<T>()`, which lives in the binder package. Check `Directory.Packages.props` for a `PackageVersion` entry named `Microsoft.Extensions.Configuration.Binder`; if it is missing, add one alongside the other `Microsoft.Extensions.*` entries using the same version as `Microsoft.Extensions.Options.ConfigurationExtensions`.

Then add to `src/Alberto.Dcb/Alberto.Dcb.csproj`, inside the existing `<ItemGroup>` of `PackageReference` elements:

```xml
<PackageReference Include="Microsoft.Extensions.Configuration.Binder" />
<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
```

- [ ] **Step 7: Write the defaults test**

Create `tests/Alberto.Dcb.Tests/Configuration/OptionsDefaultsTests.cs`:

```csharp
using Alberto.Dcb.Configuration;
using FluentAssertions;

namespace Alberto.Dcb.Tests.Configuration;

public class OptionsDefaultsTests
{
    [Fact]
    public void ControlLoopOptions_defaults_match_the_documented_values()
    {
        var options = new ControlLoopOptions();

        options.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(250));
        options.BatchSize.Should().Be(100);
        options.HeadRefreshInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        options.HeadWindowSize.Should().Be(2000);
        options.Retry.MaxRetries.Should().Be(3);
        options.DeadLetterRetry.BatchSize.Should().Be(10);
        options.Leases.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ControlLoopOptions_Default_is_equal_to_a_fresh_instance()
    {
        ControlLoopOptions.Default.Should().Be(new ControlLoopOptions());
    }

    [Fact]
    public void An_empty_override_changes_nothing()
    {
        var options = new ControlLoopOptions { BatchSize = 777 };

        var result = new ControlLoopOverrides().ApplyTo(options);

        result.Should().Be(options);
    }

    [Fact]
    public void A_nested_override_replaces_only_the_named_property()
    {
        var options = new ControlLoopOptions();

        var result = new ControlLoopOverrides
        {
            Retry = new RetryOverrides { MaxRetries = 9 },
        }.ApplyTo(options);

        result.Retry.MaxRetries.Should().Be(9);
        result.Retry.RetryDelay.Should().Be(options.Retry.RetryDelay);
        result.BatchSize.Should().Be(options.BatchSize);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(20, 30000)]
    public void CalculateDelay_backs_off_exponentially_and_caps(int attempt, int expectedMilliseconds)
    {
        new RetryOptions()
            .CalculateDelay(attempt)
            .Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Fact]
    public void CheckpointOptions_defaults_to_Warn()
    {
        new CheckpointOptions().OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Warn);
    }
}
```

- [ ] **Step 8: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~Alberto.Dcb.Tests.Configuration"
```

Expected: PASS, 9 tests.

- [ ] **Step 9: Commit**

```bash
git add src/Alberto.Dcb/Configuration src/Alberto.Dcb/Alberto.Dcb.csproj Directory.Packages.props tests/Alberto.Dcb.Tests/Configuration && git commit -m "feat(config): add options records with configuration override mirrors"
```

---

### Task 2: The module definition, the backend descriptor, and configuration overlay

This task builds the immutable declaration that `DcbModuleBuilder` will accumulate, and the
extension point (`IAlbertoBackendDescriptor`) that replaces third-party access to
`builder.Services`. Nothing consumes it yet — the existing builder keeps working untouched.

**Files:**
- Create: `src/Alberto.Dcb/Configuration/ProcessorDeclaration.cs`
- Create: `src/Alberto.Dcb/Configuration/AlbertoModuleContext.cs`
- Create: `src/Alberto.Dcb/Configuration/IAlbertoBackendDescriptor.cs`
- Create: `src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/ConfigurationOverlayTests.cs`

**Interfaces:**
- Consumes: `IAlbertoOverrides<TOptions>`, `AlbertoOptionsOverlay.Overlay`, `ControlLoopOptions`/`ControlLoopOverrides`, `TelemetryOptions`/`TelemetryOverrides`, `CheckpointOptions`/`CheckpointOverrides` (Task 1); `ProcessorExecutionOptions` (existing, `Alberto.Dcb.Subscriptions`).
- Produces:
  - `ProcessorKind` enum `{ Projection, Reactor }`
  - `ProcessorDeclaration` record — `{ string ProcessorId, ProcessorKind Kind, ProcessorExecutionOptions Execution, Type? HandlerType }`
  - `AlbertoModuleContext` — `{ IServiceCollection Services, AlbertoModuleDefinition Definition }`, plus `string ModuleKey` and `bool TenancyEnabled` conveniences
  - `IAlbertoBackendDescriptor` — `{ string Name, bool SupportsTenancy, IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition), IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection), void Register(AlbertoModuleContext) }`
  - `AlbertoModuleDefinition` record + `AlbertoModuleDefinition.ApplyConfiguration(AlbertoModuleDefinition, IConfiguration)`
- Note: `IAlbertoBackendDescriptor.Validate` returns `AlbertoValidationFailure`, defined in Task 3. Implement Task 3's `AlbertoValidationFailure.cs` **first** if you are executing tasks out of order; otherwise create it here as part of Step 3 exactly as Task 3 specifies and skip that step in Task 3.

- [ ] **Step 1: Write the failing overlay test**

Create `tests/Alberto.Dcb.Tests/Configuration/ConfigurationOverlayTests.cs`:

```csharp
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Tests.Configuration;

public class ConfigurationOverlayTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static AlbertoModuleDefinition Definition() => new()
    {
        ModuleKey = "orders",
        ControlLoop = new ControlLoopOptions { BatchSize = 500 },
    };

    [Fact]
    public void Configuration_overrides_the_code_default()
    {
        var configuration = Configuration(("Alberto:Modules:orders:ControlLoop:BatchSize", "42"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.BatchSize.Should().Be(42);
    }

    [Fact]
    public void Absent_configuration_leaves_the_code_default_intact()
    {
        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), Configuration());

        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void A_present_section_only_overrides_the_keys_it_names()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:PollingInterval", "00:00:00.050"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(50));
        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void Nested_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:ControlLoop:Retry:MaxRetries", "7"),
            ("Alberto:Modules:orders:ControlLoop:Leases:Enabled", "true"),
            ("Alberto:Modules:orders:ControlLoop:Leases:ReplicaId", "pod-3"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.Retry.MaxRetries.Should().Be(7);
        result.ControlLoop.Retry.RetryDelay.Should().Be(TimeSpan.FromSeconds(1));
        result.ControlLoop.Leases.Enabled.Should().BeTrue();
        result.ControlLoop.Leases.ReplicaId.Should().Be("pod-3");
    }

    [Fact]
    public void Another_modules_section_is_ignored()
    {
        var configuration = Configuration(("Alberto:Modules:billing:ControlLoop:BatchSize", "1"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.ControlLoop.BatchSize.Should().Be(500);
    }

    [Fact]
    public void Telemetry_and_checkpoint_sections_bind()
    {
        var configuration = Configuration(
            ("Alberto:Modules:orders:Telemetry:Enabled", "false"),
            ("Alberto:Modules:orders:Checkpoints:OrphanPolicy", "Strict"));

        var result = AlbertoModuleDefinition.ApplyConfiguration(Definition(), configuration);

        result.Telemetry.Enabled.Should().BeFalse();
        result.Checkpoints.OrphanPolicy.Should().Be(OrphanCheckpointPolicy.Strict);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ConfigurationOverlayTests"
```

Expected: compile error — `The name 'AlbertoModuleDefinition' does not exist`.

- [ ] **Step 3: Create the processor declaration**

Create `src/Alberto.Dcb/Configuration/ProcessorDeclaration.cs`:

```csharp
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Configuration;

/// <summary>What kind of work a declared processor performs.</summary>
public enum ProcessorKind
{
    /// <summary>Folds events into a queryable read model.</summary>
    Projection = 0,

    /// <summary>Reacts to events with a side effect.</summary>
    Reactor = 1,
}

/// <summary>
/// A processor as declared at configuration time. This is the validator's view of the module:
/// it names every processor without resolving anything from the container.
/// </summary>
public sealed record ProcessorDeclaration
{
    /// <summary>The checkpoint key. Unique within a module.</summary>
    public required string ProcessorId { get; init; }

    /// <summary>Whether this is a projection or a reactor.</summary>
    public required ProcessorKind Kind { get; init; }

    /// <summary>How the control loop should dispatch to this processor.</summary>
    public ProcessorExecutionOptions Execution { get; init; } = ProcessorExecutionOptions.Default;

    /// <summary>
    /// The handler type the processor id was derived from, when there is one.
    /// Null for processors registered from a bare lambda.
    /// </summary>
    public Type? HandlerType { get; init; }
}
```

- [ ] **Step 4: Create the registration context and the backend descriptor**

Create `src/Alberto.Dcb/Configuration/AlbertoModuleContext.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// What a deferred registration callback receives. Created once per module, after the
/// declaration is complete and configuration has been overlaid — so
/// <see cref="Definition"/> is final and reading it is never order-dependent.
/// </summary>
public sealed class AlbertoModuleContext
{
    internal AlbertoModuleContext(IServiceCollection services, AlbertoModuleDefinition definition)
    {
        Services = services;
        Definition = definition;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The complete, immutable module declaration.</summary>
    public AlbertoModuleDefinition Definition { get; }

    /// <summary>Shorthand for <c>Definition.ModuleKey</c>. Use it as the DI service key.</summary>
    public string ModuleKey => Definition.ModuleKey;

    /// <summary>Shorthand for <c>Definition.TenancyEnabled</c>.</summary>
    public bool TenancyEnabled => Definition.TenancyEnabled;
}
```

Create `src/Alberto.Dcb/Configuration/IAlbertoBackendDescriptor.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// A storage backend as declared by <c>.WithPostgres(...)</c>, <c>.WithInMemory()</c> and friends.
/// This is the supported extension point for third-party backends; it replaces reaching into
/// <c>DcbModuleBuilder.Services</c> at declaration time.
/// </summary>
/// <remarks>
/// Implementations are immutable value objects. <see cref="ApplyConfiguration"/> returns a new
/// instance rather than mutating, so a backend can be declared before or after any other call in
/// the module lambda without changing the result.
/// </remarks>
public interface IAlbertoBackendDescriptor
{
    /// <summary>Human-readable backend name, used in validation messages. For example "Postgres".</summary>
    string Name { get; }

    /// <summary>Whether this backend can serve a module declared with <c>.WithTenancy()</c>.</summary>
    bool SupportsTenancy { get; }

    /// <summary>
    /// Overlays this backend's own options from the module's configuration section
    /// (already scoped to <c>Alberto:Modules:{moduleKey}</c>). Return <c>this</c> when there is
    /// nothing to bind.
    /// </summary>
    IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection);

    /// <summary>
    /// Reports backend-specific configuration problems. Called during startup validation,
    /// before any service is resolved, so it must not open connections or touch the container.
    /// </summary>
    IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition);

    /// <summary>
    /// Registers the backend's services. Called once, after validation, with the final definition.
    /// </summary>
    void Register(AlbertoModuleContext context);
}
```

> If Task 3 has not been implemented yet, create `AlbertoValidationFailure.cs` now using the
> code given in Task 3 Step 3, and skip that step when you reach Task 3.

- [ ] **Step 5: Create the module definition**

Create `src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// The complete, immutable declaration of one Alberto module: what backend it uses, what
/// processors it runs, and how the control loop behaves. Registered as a named options
/// instance keyed by <see cref="ModuleKey"/> and validated at startup.
/// </summary>
public sealed record AlbertoModuleDefinition
{
    /// <summary>The module key passed to <c>AddAlberto</c>. Also the DI service key.</summary>
    public required string ModuleKey { get; init; }

    /// <summary>Whether <c>.WithTenancy()</c> was called.</summary>
    public bool TenancyEnabled { get; init; }

    /// <summary>The declared storage backend, or null when none was declared.</summary>
    public IAlbertoBackendDescriptor? Backend { get; init; }

    /// <summary>Control loop settings.</summary>
    public ControlLoopOptions ControlLoop { get; init; } = new();

    /// <summary>Telemetry settings. Only meaningful when <c>.WithTelemetry()</c> was called.</summary>
    public TelemetryOptions Telemetry { get; init; } = new();

    /// <summary>Checkpoint hygiene settings.</summary>
    public CheckpointOptions Checkpoints { get; init; } = new();

    /// <summary>Whether <c>.WithTelemetry()</c> was called.</summary>
    public bool TelemetryEnabled { get; init; }

    /// <summary>Every processor declared on this module.</summary>
    public ImmutableArray<ProcessorDeclaration> Processors { get; init; } = [];

    /// <summary>The configuration path this module binds from.</summary>
    public string ConfigurationPath => $"Alberto:Modules:{ModuleKey}";

    /// <summary>
    /// Returns <paramref name="definition"/> with every value found under
    /// <c>Alberto:Modules:{ModuleKey}</c> applied on top of the code-configured defaults.
    /// </summary>
    public static AlbertoModuleDefinition ApplyConfiguration(
        AlbertoModuleDefinition definition,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(definition.ConfigurationPath);

        return definition with
        {
            ControlLoop = AlbertoOptionsOverlay.Overlay<ControlLoopOptions, ControlLoopOverrides>(
                section, "ControlLoop", definition.ControlLoop),
            Telemetry = AlbertoOptionsOverlay.Overlay<TelemetryOptions, TelemetryOverrides>(
                section, "Telemetry", definition.Telemetry),
            Checkpoints = AlbertoOptionsOverlay.Overlay<CheckpointOptions, CheckpointOverrides>(
                section, "Checkpoints", definition.Checkpoints),
            Backend = definition.Backend?.ApplyConfiguration(section),
        };
    }
}
```

- [ ] **Step 6: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ConfigurationOverlayTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 7: Build the whole solution**

```bash
dotnet build
```

Expected: `Build succeeded` with 0 warnings and 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Alberto.Dcb/Configuration tests/Alberto.Dcb.Tests/Configuration && git commit -m "feat(config): add module definition, backend descriptor and configuration overlay"
```

---

### Task 3: Startup validation and its error messages

Every misconfiguration the old code discovered late — inside an `IHostedService` factory lambda,
one exception at a time — becomes a validation failure reported at startup, all of them at once.

**Files:**
- Create: `src/Alberto.Dcb/Configuration/AlbertoValidationFailure.cs` (skip if already created in Task 2)
- Create: `src/Alberto.Dcb/Configuration/AlbertoModuleValidator.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/ValidationMessageTests.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/AlbertoModuleValidatorTests.cs`

**Interfaces:**
- Consumes: `AlbertoModuleDefinition`, `ProcessorDeclaration`, `ProcessorKind`, `IAlbertoBackendDescriptor` (Task 2); `ProcessorBatchingMode` (existing).
- Produces:
  - `AlbertoValidationFailure` record — `{ string Code, string Problem, string Remedy }`, plus `string Format()`
  - `AlbertoValidationReport` — `static string Describe(string moduleKey, IReadOnlyList<AlbertoValidationFailure> failures)`
  - `AlbertoModuleValidator : IValidateOptions<AlbertoModuleDefinition>`

**Validation catalog.** Core codes are `ALB0xxx`; backend packages use their own range (Postgres uses `ALB1xxx`, Task 8).

| Code | Problem |
|---|---|
| `ALB0001` | No event store backend declared |
| `ALB0002` | Two processors share a processor id |
| `ALB0003` | `.WithTenancy()` declared but the backend does not support tenancy |
| `ALB0004` | `PollingInterval`, `BatchSize`, `HeadRefreshInterval` or `HeadWindowSize` is not positive |
| `ALB0005` | `MaxConcurrency > 1` while `BatchingMode` is `Disabled` |
| `ALB0006` | A processor id is empty or contains whitespace |
| `ALB0007` | `Retry.MaxRetries` is negative, or `Retry.BackoffMultiplier` is below 1.0 |

- [ ] **Step 1: Write the failing message-formatting test**

Create `tests/Alberto.Dcb.Tests/Configuration/ValidationMessageTests.cs`:

```csharp
using Alberto.Dcb.Configuration;
using FluentAssertions;

namespace Alberto.Dcb.Tests.Configuration;

public class ValidationMessageTests
{
    [Fact]
    public void A_failure_renders_its_code_problem_and_remedy()
    {
        var failure = new AlbertoValidationFailure(
            "ALB0001",
            "No event store backend is configured.",
            "Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"orders\", ...).");

        failure.Format().Should().Be(
            "[ALB0001] No event store backend is configured." + Environment.NewLine +
            "          → Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"orders\", ...).");
    }

    [Fact]
    public void The_report_names_the_module_and_counts_the_problems()
    {
        var report = AlbertoValidationReport.Describe("orders",
        [
            new AlbertoValidationFailure("ALB0001", "First problem.", "First remedy."),
            new AlbertoValidationFailure("ALB0002", "Second problem.", "Second remedy."),
        ]);

        report.Should().StartWith("Alberto module 'orders' cannot start: 2 configuration problems.");
        report.Should().Contain("[ALB0001] First problem.");
        report.Should().Contain("[ALB0002] Second problem.");
        report.Should().Contain("→ Second remedy.");
    }

    [Fact]
    public void One_problem_is_reported_in_the_singular()
    {
        var report = AlbertoValidationReport.Describe("orders",
            [new AlbertoValidationFailure("ALB0001", "Only problem.", "Only remedy.")]);

        report.Should().StartWith("Alberto module 'orders' cannot start: 1 configuration problem.");
    }

    [Fact]
    public void The_report_ends_with_the_configuration_path_hint()
    {
        var report = AlbertoValidationReport.Describe("orders",
            [new AlbertoValidationFailure("ALB0001", "Problem.", "Remedy.")]);

        report.Should().EndWith(
            "Settings can also be supplied under 'Alberto:Modules:orders' in configuration.");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ValidationMessageTests"
```

Expected: compile error — `The name 'AlbertoValidationFailure' does not exist`.

- [ ] **Step 3: Create the failure record and the report formatter**

Create `src/Alberto.Dcb/Configuration/AlbertoValidationFailure.cs`:

```csharp
using System.Text;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// One thing wrong with a module's configuration, stated as a problem plus the specific edit
/// that fixes it.
/// </summary>
/// <param name="Code">Stable identifier, for example <c>ALB0001</c>. Safe to grep for and to link docs from.</param>
/// <param name="Problem">What is wrong, in one sentence, naming the offending value.</param>
/// <param name="Remedy">The concrete change to make, naming the method or configuration key.</param>
public sealed record AlbertoValidationFailure(string Code, string Problem, string Remedy)
{
    /// <summary>Renders this failure as two indented lines.</summary>
    public string Format() =>
        $"[{Code}] {Problem}{Environment.NewLine}          → {Remedy}";
}

/// <summary>
/// Renders a module's validation failures into the message attached to the
/// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/> thrown at startup.
/// </summary>
public static class AlbertoValidationReport
{
    /// <summary>
    /// Describes every failure for one module. The message is what a developer sees in the
    /// console when the host refuses to start, so it names the module, counts the problems,
    /// and closes with where else these settings can come from.
    /// </summary>
    public static string Describe(string moduleKey, IReadOnlyList<AlbertoValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(moduleKey);
        ArgumentNullException.ThrowIfNull(failures);

        var builder = new StringBuilder();
        var noun = failures.Count == 1 ? "problem" : "problems";

        builder.Append($"Alberto module '{moduleKey}' cannot start: {failures.Count} configuration {noun}.");
        builder.AppendLine();

        foreach (var failure in failures)
        {
            builder.AppendLine();
            builder.AppendLine("  " + failure.Format());
        }

        builder.AppendLine();
        builder.Append(
            $"Settings can also be supplied under 'Alberto:Modules:{moduleKey}' in configuration.");

        return builder.ToString();
    }
}
```

- [ ] **Step 4: Run the message tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ValidationMessageTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing validator test**

Create `tests/Alberto.Dcb.Tests/Configuration/AlbertoModuleValidatorTests.cs`:

```csharp
using System.Collections.Immutable;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.Tests.Configuration;

public class AlbertoModuleValidatorTests
{
    private sealed class FakeBackend(bool supportsTenancy = true, params AlbertoValidationFailure[] failures)
        : IAlbertoBackendDescriptor
    {
        public string Name => "Fake";
        public bool SupportsTenancy => supportsTenancy;
        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => failures;
        public void Register(AlbertoModuleContext context) { }
    }

    private static AlbertoModuleDefinition Valid(params ProcessorDeclaration[] processors) => new()
    {
        ModuleKey = "orders",
        Backend = new FakeBackend(),
        Processors = [.. processors],
    };

    private static ProcessorDeclaration Processor(
        string id,
        ProcessorExecutionOptions? execution = null) => new()
    {
        ProcessorId = id,
        Kind = ProcessorKind.Reactor,
        Execution = execution ?? ProcessorExecutionOptions.Default,
    };

    private static IReadOnlyList<AlbertoValidationFailure> Run(AlbertoModuleDefinition definition) =>
        new AlbertoModuleValidator().Collect(definition);

    [Fact]
    public void A_well_formed_module_produces_no_failures()
    {
        Run(Valid(Processor("orders-summary"))).Should().BeEmpty();
    }

    [Fact]
    public void A_module_without_a_backend_fails_with_ALB0001()
    {
        var failures = Run(Valid() with { Backend = null });

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0001");
    }

    [Fact]
    public void Duplicate_processor_ids_fail_with_ALB0002()
    {
        var failures = Run(Valid(Processor("same"), Processor("same")));

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0002");
        failures[0].Problem.Should().Contain("same");
    }

    [Fact]
    public void Tenancy_on_a_backend_that_does_not_support_it_fails_with_ALB0003()
    {
        var definition = Valid() with { TenancyEnabled = true, Backend = new FakeBackend(supportsTenancy: false) };

        Run(definition).Should().ContainSingle().Which.Code.Should().Be("ALB0003");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(250, 0)]
    [InlineData(250, -5)]
    public void Non_positive_control_loop_values_fail_with_ALB0004(int pollingMilliseconds, int batchSize)
    {
        var definition = Valid() with
        {
            ControlLoop = new ControlLoopOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(pollingMilliseconds),
                BatchSize = batchSize,
            },
        };

        Run(definition).Should().Contain(f => f.Code == "ALB0004");
    }

    [Fact]
    public void Concurrency_without_batching_fails_with_ALB0005()
    {
        var execution = new ProcessorExecutionOptions(ProcessorBatchingMode.Disabled, MaxConcurrency: 4);

        var failures = Run(Valid(Processor("busy", execution)));

        failures.Should().ContainSingle().Which.Code.Should().Be("ALB0005");
        failures[0].Problem.Should().Contain("busy");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("has space")]
    public void A_malformed_processor_id_fails_with_ALB0006(string processorId)
    {
        Run(Valid(Processor(processorId))).Should().Contain(f => f.Code == "ALB0006");
    }

    [Fact]
    public void A_negative_retry_count_fails_with_ALB0007()
    {
        var definition = Valid() with
        {
            ControlLoop = new ControlLoopOptions { Retry = new RetryOptions { MaxRetries = -1 } },
        };

        Run(definition).Should().Contain(f => f.Code == "ALB0007");
    }

    [Fact]
    public void Backend_failures_are_included()
    {
        var backendFailure = new AlbertoValidationFailure("ALB9999", "Backend problem.", "Backend remedy.");
        var definition = Valid() with { Backend = new FakeBackend(true, backendFailure) };

        Run(definition).Should().Contain(backendFailure);
    }

    [Fact]
    public void Every_failure_is_reported_at_once_rather_than_the_first()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with
        {
            Backend = null,
            ControlLoop = new ControlLoopOptions { BatchSize = 0 },
        };

        Run(definition).Select(f => f.Code).Should().Contain(["ALB0001", "ALB0002", "ALB0004"]);
    }

    [Fact]
    public void Validate_fails_with_a_message_naming_every_problem()
    {
        var definition = Valid(Processor("dup"), Processor("dup")) with { Backend = null };

        var result = new AlbertoModuleValidator().Validate("orders", definition);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ALB0001").And.Contain("ALB0002");
    }

    [Fact]
    public void Validate_succeeds_for_a_well_formed_module()
    {
        new AlbertoModuleValidator()
            .Validate("orders", Valid(Processor("ok")))
            .Succeeded.Should().BeTrue();
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~AlbertoModuleValidatorTests"
```

Expected: compile error — `The name 'AlbertoModuleValidator' does not exist`.

- [ ] **Step 7: Create the validator**

Create `src/Alberto.Dcb/Configuration/AlbertoModuleValidator.cs`:

```csharp
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Checks a module declaration at startup, under <c>ValidateOnStart()</c>. Collects every
/// problem rather than throwing on the first, so one restart surfaces the whole list.
/// </summary>
public sealed class AlbertoModuleValidator : IValidateOptions<AlbertoModuleDefinition>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AlbertoModuleDefinition options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = Collect(options);
        if (failures.Count == 0)
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(
            AlbertoValidationReport.Describe(options.ModuleKey, failures));
    }

    /// <summary>
    /// Returns every configuration problem in <paramref name="definition"/>. Exposed separately
    /// so tests and diagnostics can inspect codes instead of parsing a message.
    /// </summary>
    public IReadOnlyList<AlbertoValidationFailure> Collect(AlbertoModuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var failures = new List<AlbertoValidationFailure>();

        ValidateBackend(definition, failures);
        ValidateControlLoop(definition, failures);
        ValidateProcessors(definition, failures);

        if (definition.Backend is not null)
            failures.AddRange(definition.Backend.Validate(definition));

        return failures;
    }

    private static void ValidateBackend(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        if (definition.Backend is null)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0001",
                "No event store backend is configured.",
                $"Add .WithPostgres(...) or .WithInMemory() inside AddAlberto(\"{definition.ModuleKey}\", ...)."));
            return;
        }

        if (definition.TenancyEnabled && !definition.Backend.SupportsTenancy)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0003",
                $"The module declares .WithTenancy() but the {definition.Backend.Name} backend does not support tenancy.",
                "Remove .WithTenancy() or switch to a backend that supports it, such as .WithPostgres(...)."));
        }
    }

    private static void ValidateControlLoop(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        var loop = definition.ControlLoop;
        var path = definition.ConfigurationPath;

        if (loop.PollingInterval <= TimeSpan.Zero)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.PollingInterval is {loop.PollingInterval}, which is not a positive duration.",
                $"Set a positive interval via .WithControlLoop(o => o with {{ PollingInterval = ... }}) or '{path}:ControlLoop:PollingInterval'."));
        }

        if (loop.HeadRefreshInterval <= TimeSpan.Zero)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.HeadRefreshInterval is {loop.HeadRefreshInterval}, which is not a positive duration.",
                $"Set a positive interval via .WithControlLoop(o => o with {{ HeadRefreshInterval = ... }}) or '{path}:ControlLoop:HeadRefreshInterval'."));
        }

        if (loop.BatchSize <= 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.BatchSize is {loop.BatchSize}, which is not a positive count.",
                $"Set a positive batch size via .WithControlLoop(o => o with {{ BatchSize = ... }}) or '{path}:ControlLoop:BatchSize'."));
        }

        if (loop.HeadWindowSize <= 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0004",
                $"ControlLoop.HeadWindowSize is {loop.HeadWindowSize}, which is not a positive count.",
                $"Set a positive window size via .WithControlLoop(o => o with {{ HeadWindowSize = ... }}) or '{path}:ControlLoop:HeadWindowSize'."));
        }

        if (loop.Retry.MaxRetries < 0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0007",
                $"ControlLoop.Retry.MaxRetries is {loop.Retry.MaxRetries}. Use 0 to disable retries.",
                $"Set a non-negative count via '{path}:ControlLoop:Retry:MaxRetries'."));
        }

        if (loop.Retry.BackoffMultiplier < 1.0)
        {
            failures.Add(new AlbertoValidationFailure(
                "ALB0007",
                $"ControlLoop.Retry.BackoffMultiplier is {loop.Retry.BackoffMultiplier}, which would shrink the delay on each retry.",
                $"Use 1.0 for a constant delay, or a larger value to back off, via '{path}:ControlLoop:Retry:BackoffMultiplier'."));
        }
    }

    private static void ValidateProcessors(AlbertoModuleDefinition definition, List<AlbertoValidationFailure> failures)
    {
        foreach (var duplicate in definition.Processors
                     .GroupBy(p => p.ProcessorId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var types = duplicate
                .Select(p => p.HandlerType?.Name)
                .Where(n => n is not null)
                .ToArray();

            var attribution = types.Length > 0
                ? $" Declared by {string.Join(" and ", types)}."
                : string.Empty;

            failures.Add(new AlbertoValidationFailure(
                "ALB0002",
                $"{duplicate.Count()} processors share the id '{duplicate.Key}'. Processor ids are checkpoint keys and must be unique within a module.{attribution}",
                "Give one of them a distinct id with [ProcessorId(\"...\")] on its handler type."));
        }

        foreach (var processor in definition.Processors)
        {
            if (string.IsNullOrWhiteSpace(processor.ProcessorId)
                || processor.ProcessorId.Any(char.IsWhiteSpace))
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0006",
                    $"The processor id '{processor.ProcessorId}' is empty or contains whitespace.",
                    "Processor ids are used as checkpoint keys. Use a non-empty identifier without whitespace."));
            }

            if (processor.Execution is { MaxConcurrency: > 1, BatchingMode: ProcessorBatchingMode.Disabled })
            {
                failures.Add(new AlbertoValidationFailure(
                    "ALB0005",
                    $"Processor '{processor.ProcessorId}' asks for MaxConcurrency {processor.Execution.MaxConcurrency} while batching is Disabled. Concurrency only applies within a batch.",
                    "Set BatchingMode to Required or IfSupported, or set MaxConcurrency back to 1."));
            }
        }
    }
}
```

- [ ] **Step 8: Run the validator tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~AlbertoModuleValidatorTests"
```

Expected: PASS, 16 tests.

- [ ] **Step 9: Commit**

```bash
git add src/Alberto.Dcb/Configuration tests/Alberto.Dcb.Tests/Configuration && git commit -m "feat(config): add startup validation with an actionable failure report"
```

---

### Task 4: Derived processor identity

A processor id is a checkpoint key: get it wrong and the processor silently replays from zero.
Today it is a free-form string every caller must invent and keep stable forever. This task
derives it from the handler type and makes the string an explicit, attributed override.

**Files:**
- Create: `src/Alberto.Dcb/Configuration/ProcessorIdAttribute.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/ProcessorIdTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `[ProcessorId("...")]` — `ProcessorIdAttribute` with `string Id`, usable on classes and structs
  - `static class ProcessorId` with `string For<THandler>()` and `string For(Type handlerType)`

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/ProcessorIdTests.cs`:

```csharp
using Alberto.Dcb.Configuration;
using FluentAssertions;

namespace Alberto.Dcb.Tests.Configuration;

public class ProcessorIdTests
{
    private sealed class OrderSummaryHandler;

    [ProcessorId("orders.legacy-summary")]
    private sealed class RenamedHandler;

    private sealed class GenericHandler<T>;

    private sealed class Outer
    {
        internal sealed class Inner;
    }

    [ProcessorId("  ")]
    private sealed class BlankIdHandler;

    [Fact]
    public void An_unattributed_type_derives_its_own_name()
    {
        ProcessorId.For<OrderSummaryHandler>().Should().Be("OrderSummaryHandler");
    }

    [Fact]
    public void The_attribute_wins_over_the_derived_name()
    {
        ProcessorId.For<RenamedHandler>().Should().Be("orders.legacy-summary");
    }

    [Fact]
    public void A_nested_type_is_qualified_by_its_declaring_type()
    {
        ProcessorId.For<Outer.Inner>().Should().Be("Outer.Inner");
    }

    [Fact]
    public void A_generic_type_includes_its_argument()
    {
        ProcessorId.For<GenericHandler<OrderSummaryHandler>>()
            .Should().Be("GenericHandler_OrderSummaryHandler");
    }

    [Fact]
    public void Derivation_is_stable_across_calls()
    {
        ProcessorId.For<OrderSummaryHandler>().Should().Be(ProcessorId.For<OrderSummaryHandler>());
    }

    [Fact]
    public void A_blank_attribute_id_throws_at_the_point_of_declaration()
    {
        var act = () => ProcessorId.For<BlankIdHandler>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BlankIdHandler*ProcessorId*");
    }

    [Fact]
    public void A_null_type_is_rejected()
    {
        var act = () => ProcessorId.For(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ProcessorIdTests"
```

Expected: compile error — `The name 'ProcessorId' does not exist`.

- [ ] **Step 3: Create the attribute and the resolver**

Create `src/Alberto.Dcb/Configuration/ProcessorIdAttribute.cs`:

```csharp
using System.Reflection;

namespace Alberto.Dcb.Configuration;

/// <summary>
/// Pins a processor's checkpoint key to a fixed string, independent of the type's name.
/// </summary>
/// <remarks>
/// Apply this when renaming a handler whose checkpoint must survive the rename, or when the
/// derived name would collide with another processor in the same module. Once a processor has
/// run in production its id is data: changing it restarts the processor from position zero.
/// </remarks>
/// <example>
/// <code>
/// [ProcessorId("orders.summary")]
/// public sealed class OrderSummaryReactor { }
/// </code>
/// </example>
/// <param name="id">The checkpoint key. Must be non-empty and contain no whitespace.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ProcessorIdAttribute(string id) : Attribute
{
    /// <summary>The checkpoint key this handler uses.</summary>
    public string Id { get; } = id;
}

/// <summary>
/// Derives the checkpoint key for a processor from its handler type.
/// </summary>
public static class ProcessorId
{
    /// <summary>Returns the processor id for <typeparamref name="THandler"/>.</summary>
    public static string For<THandler>() => For(typeof(THandler));

    /// <summary>
    /// Returns the processor id for <paramref name="handlerType"/>: the value of its
    /// <see cref="ProcessorIdAttribute"/> when present, otherwise the type's name qualified by
    /// any declaring types and generic arguments.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type carries a <see cref="ProcessorIdAttribute"/> whose id is empty or contains whitespace.
    /// </exception>
    public static string For(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);

        var attribute = handlerType.GetCustomAttribute<ProcessorIdAttribute>(inherit: false);
        if (attribute is null)
            return Describe(handlerType);

        if (string.IsNullOrWhiteSpace(attribute.Id) || attribute.Id.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"[ProcessorId(\"{attribute.Id}\")] on {handlerType.Name} is not a usable checkpoint key. " +
                "Processor ids must be non-empty and contain no whitespace.");
        }

        return attribute.Id;
    }

    private static string Describe(Type type)
    {
        var name = type.Name;

        var arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
            name = name[..arity];

        if (type.IsGenericType)
            name = $"{name}_{string.Join('_', type.GetGenericArguments().Select(Describe))}";

        return type.DeclaringType is null ? name : $"{Describe(type.DeclaringType)}.{name}";
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ProcessorIdTests"
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Alberto.Dcb/Configuration/ProcessorIdAttribute.cs tests/Alberto.Dcb.Tests/Configuration/ProcessorIdTests.cs && git commit -m "feat(config): derive processor ids from handler types with an attribute override"
```

---

### Task 5: Deferred declaration in `DcbModuleBuilder` and named options in `AddAlberto`

This is the pivot. `DcbModuleBuilder` starts accumulating an `AlbertoModuleDefinition` and a list
of deferred registration callbacks; `AddAlberto` registers the definition as a named options
instance, overlays configuration, validates on start, and only then runs the callbacks.

`builder.Services` **stays** for now so every existing `.WithPostgres()` / `.WithInMemory()` /
`.WithTelemetry()` extension keeps compiling and the suite stays green. Tasks 8–11 migrate them
to descriptors one at a time; Task 12 removes `Services`.

**Files:**
- Modify: `src/Alberto.Dcb/DcbModuleBuilder.cs` (full rewrite)
- Modify: `src/Alberto.Dcb/ServiceCollectionExtensions.cs` (full rewrite)
- Test: `tests/Alberto.Dcb.Tests/Configuration/ModuleDefinitionTests.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/StartupValidationTests.cs`

**Interfaces:**
- Consumes: `AlbertoModuleDefinition`, `AlbertoModuleContext`, `IAlbertoBackendDescriptor`, `ProcessorDeclaration` (Task 2); `AlbertoModuleValidator` (Task 3).
- Produces, on `DcbModuleBuilder`:
  - `string ModuleKey { get; }`
  - `IServiceCollection Services { get; }` — **deprecated, removed in Task 12**
  - `DcbModuleBuilder WithTenancy()`
  - `DcbModuleBuilder Configure(Func<AlbertoModuleDefinition, AlbertoModuleDefinition> configure)` — the single mutation primitive every `With*` extension uses
  - `DcbModuleBuilder UseBackend(IAlbertoBackendDescriptor descriptor)`
  - `DcbModuleBuilder DeclareProcessor(ProcessorDeclaration declaration)`
  - `DcbModuleBuilder Register(Action<AlbertoModuleContext> register)` — defer a DI registration until the definition is final
  - `internal AlbertoModuleDefinition Definition { get; }`
  - `internal IReadOnlyList<Action<AlbertoModuleContext>> DeferredRegistrations { get; }`
  - `internal bool HasTenancy { get; }` and `internal bool ControlLoopConfigured { get; set; }` — retained for the still-unmigrated extensions
- Produces, on `ServiceCollectionExtensions`: `AddAlberto(this IServiceCollection services, string moduleKey, Action<DcbModuleBuilder> configure)` (signature unchanged).

- [ ] **Step 1: Write the failing declaration test**

Create `tests/Alberto.Dcb.Tests/Configuration/ModuleDefinitionTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class ModuleDefinitionTests
{
    private sealed class FakeBackend : IAlbertoBackendDescriptor
    {
        public string Name => "Fake";
        public bool SupportsTenancy => true;
        public bool Registered { get; private set; }
        public bool TenancyAtRegistration { get; private set; }

        public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;
        public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

        public void Register(AlbertoModuleContext context)
        {
            Registered = true;
            TenancyAtRegistration = context.TenancyEnabled;
        }
    }

    private static AlbertoModuleDefinition Resolve(IServiceCollection services, string moduleKey) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

    [Fact]
    public void The_definition_is_resolvable_as_a_named_options_instance()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.UseBackend(new FakeBackend()));

        Resolve(services, "orders").ModuleKey.Should().Be("orders");
    }

    [Fact]
    public void Two_modules_keep_separate_definitions()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.UseBackend(new FakeBackend()));
        services.AddAlberto("billing", module => module
            .UseBackend(new FakeBackend())
            .Configure(d => d with { ControlLoop = d.ControlLoop with { BatchSize = 5 } }));

        var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>();

        monitor.Get("orders").ControlLoop.BatchSize.Should().Be(100);
        monitor.Get("billing").ControlLoop.BatchSize.Should().Be(5);
    }

    [Fact]
    public void Backends_are_registered_after_the_whole_lambda_has_run()
    {
        var backend = new FakeBackend();
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .UseBackend(backend)
            .WithTenancy());

        backend.Registered.Should().BeTrue();
        backend.TenancyAtRegistration.Should().BeTrue(
            "deferred registration must see the final definition regardless of call order");
    }

    [Fact]
    public void Declaration_order_does_not_change_the_definition()
    {
        var tenancyFirst = new ServiceCollection();
        tenancyFirst.AddAlberto("orders", m => m.WithTenancy().UseBackend(new FakeBackend()));

        var tenancyLast = new ServiceCollection();
        tenancyLast.AddAlberto("orders", m => m.UseBackend(new FakeBackend()).WithTenancy());

        Resolve(tenancyFirst, "orders").TenancyEnabled
            .Should().Be(Resolve(tenancyLast, "orders").TenancyEnabled);
    }

    [Fact]
    public void Configuration_overrides_what_the_lambda_set()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:ControlLoop:BatchSize"] = "17",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .Configure(d => d with { ControlLoop = d.ControlLoop with { BatchSize = 500 } }));

        Resolve(services, "orders").ControlLoop.BatchSize.Should().Be(17);
    }

    [Fact]
    public void Declared_processors_appear_in_the_definition()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .DeclareProcessor(new ProcessorDeclaration
            {
                ProcessorId = "summary",
                Kind = ProcessorKind.Projection,
            }));

        Resolve(services, "orders").Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("summary");
    }

    [Fact]
    public void Deferred_registrations_run_against_the_final_definition()
    {
        string? seenModuleKey = null;
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .Register(context => seenModuleKey = context.ModuleKey)
            .UseBackend(new FakeBackend()));

        seenModuleKey.Should().Be("orders");
    }

    [Fact]
    public void A_second_backend_declaration_is_rejected_immediately()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAlberto("orders", module => module
            .UseBackend(new FakeBackend())
            .UseBackend(new FakeBackend()));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already declares*Fake*");
    }
}
```

- [ ] **Step 2: Write the failing startup-validation test**

Create `tests/Alberto.Dcb.Tests/Configuration/StartupValidationTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class StartupValidationTests
{
    private static IHost BuildHost(Action<DcbModuleBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", configure);
        return builder.Build();
    }

    [Fact]
    public async Task A_module_without_a_backend_refuses_to_start()
    {
        using var host = BuildHost(_ => { });

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("ALB0001");
    }

    [Fact]
    public async Task The_failure_message_names_the_module_and_the_remedy()
    {
        using var host = BuildHost(_ => { });

        var act = async () => await host.StartAsync(TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<OptionsValidationException>();
        exception.Which.Message.Should().Contain("Alberto module 'orders' cannot start");
        exception.Which.Message.Should().Contain("AddAlberto(\"orders\", ...)");
        exception.Which.Message.Should().Contain("Alberto:Modules:orders");
    }
}
```

> `Host.CreateApplicationBuilder()` needs `Microsoft.Extensions.Hosting`. Add a
> `PackageVersion` entry for `Microsoft.Extensions.Hosting` to `Directory.Packages.props` if one
> is not already there, then add `<PackageReference Include="Microsoft.Extensions.Hosting" />`
> to `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`.

- [ ] **Step 3: Run both to verify they fail**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ModuleDefinitionTests|FullyQualifiedName~StartupValidationTests"
```

Expected: compile error — `'DcbModuleBuilder' does not contain a definition for 'UseBackend'`.

- [ ] **Step 4: Rewrite `DcbModuleBuilder`**

Replace the entire contents of `src/Alberto.Dcb/DcbModuleBuilder.cs`:

```csharp
using Alberto.Dcb.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Declares one Alberto module. Every call records intent into an immutable
/// <see cref="AlbertoModuleDefinition"/>; nothing is registered and no I/O happens until the
/// whole lambda has run. Call order therefore never changes the result.
/// </summary>
public sealed class DcbModuleBuilder
{
    private readonly List<Action<AlbertoModuleContext>> _deferredRegistrations = [];

    internal DcbModuleBuilder(IServiceCollection services, string moduleKey)
    {
        Services = services;
        Definition = new AlbertoModuleDefinition { ModuleKey = moduleKey };
    }

    /// <summary>The module key. Doubles as the DI service key for this module's services.</summary>
    public string ModuleKey => Definition.ModuleKey;

    /// <summary>
    /// The application's service collection.
    /// </summary>
    [Obsolete("Registering services directly makes configuration order-dependent. " +
              "Use Register(context => ...) for a deferred registration, or implement " +
              "IAlbertoBackendDescriptor for a storage backend. This property is removed in 1.0.")]
    public IServiceCollection Services { get; }

    internal AlbertoModuleDefinition Definition { get; private set; }

    internal IReadOnlyList<Action<AlbertoModuleContext>> DeferredRegistrations => _deferredRegistrations;

    internal bool HasTenancy => Definition.TenancyEnabled;

    internal bool ControlLoopConfigured { get; set; }

    /// <summary>
    /// Applies <paramref name="configure"/> to this module's declaration. This is the single
    /// mutation primitive; every <c>With*</c> extension is built on it.
    /// </summary>
    public DcbModuleBuilder Configure(Func<AlbertoModuleDefinition, AlbertoModuleDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Definition = configure(Definition)
            ?? throw new InvalidOperationException("A module configuration callback returned null.");

        return this;
    }

    /// <summary>
    /// Declares which storage backend this module uses. A module has exactly one backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">A backend was already declared.</exception>
    public DcbModuleBuilder UseBackend(IAlbertoBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (Definition.Backend is { } existing)
        {
            throw new InvalidOperationException(
                $"Module '{ModuleKey}' already declares the {existing.Name} backend, so it cannot also " +
                $"use {descriptor.Name}. Each module has exactly one event store backend.");
        }

        return Configure(d => d with { Backend = descriptor });
    }

    /// <summary>Records a processor so startup validation can see it without resolving services.</summary>
    public DcbModuleBuilder DeclareProcessor(ProcessorDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        return Configure(d => d with { Processors = d.Processors.Add(declaration) });
    }

    /// <summary>
    /// Defers a service registration until the declaration is complete. The callback receives the
    /// final definition, so it can branch on tenancy or options that were declared later in the chain.
    /// </summary>
    public DcbModuleBuilder Register(Action<AlbertoModuleContext> register)
    {
        ArgumentNullException.ThrowIfNull(register);

        _deferredRegistrations.Add(register);
        return this;
    }

    /// <summary>
    /// Declares that this module's data is partitioned per tenant. The backend must support it.
    /// </summary>
    public DcbModuleBuilder WithTenancy() => Configure(d => d with { TenancyEnabled = true });
}
```

- [ ] **Step 5: Rewrite `AddAlberto`**

Replace the entire contents of `src/Alberto.Dcb/ServiceCollectionExtensions.cs`:

```csharp
using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for registering Alberto DCB modules.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds an Alberto DCB module. The <paramref name="configure"/> callback only declares
    /// intent — services are registered, and configuration is bound, after it returns. Call order
    /// inside the callback does not affect the outcome.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">Unique key for this module. Used as the DI service key and as the
    /// configuration path <c>Alberto:Modules:{moduleKey}</c>.</param>
    /// <param name="configure">Declares the module's backend, processors and options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", module => module
    ///     .WithPostgres(o => o with { ConnectionString = connectionString, Schema = "orders" })
    ///     .WithControlLoop(o => o with { BatchSize = 500 }));
    /// </code>
    /// </example>
    public static IServiceCollection AddAlberto(
        this IServiceCollection services,
        string moduleKey,
        Action<DcbModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(configure);

        // Phase 1 — declare. Runs the user's lambda against an accumulator; touches nothing else.
        var builder = new DcbModuleBuilder(services, moduleKey);
        configure(builder);

        if (!builder.ControlLoopConfigured)
            builder.WithControlLoop();

        var declared = builder.Definition;

        // Phase 2 — bind and validate. The definition becomes a named options instance so it can
        // be overlaid from configuration and checked by IValidateOptions under ValidateOnStart.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlbertoModuleDefinition>, AlbertoModuleValidator>());

        services.AddOptions<AlbertoModuleDefinition>(moduleKey)
            .Configure<IServiceProvider>((definition, provider) =>
            {
                var configuration = provider.GetService<IConfiguration>();
                var bound = configuration is null
                    ? declared
                    : AlbertoModuleDefinition.ApplyConfiguration(declared, configuration);

                CopyInto(bound, definition);
            })
            .ValidateOnStart();

        // Phase 3 — register. The definition is final, so nothing here is order-dependent.
        var final = declared;
        var context = new AlbertoModuleContext(services, final);

        final.Backend?.Register(context);

        foreach (var register in builder.DeferredRegistrations)
            register(context);

        return services;
    }

    /// <summary>
    /// The options pattern hands us a pre-constructed instance to populate, but
    /// <see cref="AlbertoModuleDefinition"/> is a record built by <c>with</c> expressions.
    /// This copies the computed record onto that instance.
    /// </summary>
    private static void CopyInto(AlbertoModuleDefinition source, AlbertoModuleDefinition target)
    {
        target.ModuleKey = source.ModuleKey;
        target.TenancyEnabled = source.TenancyEnabled;
        target.Backend = source.Backend;
        target.ControlLoop = source.ControlLoop;
        target.Telemetry = source.Telemetry;
        target.Checkpoints = source.Checkpoints;
        target.TelemetryEnabled = source.TelemetryEnabled;
        target.Processors = source.Processors;
    }
}
```

- [ ] **Step 6: Make the definition's properties settable from within the assembly**

`CopyInto` needs write access, but the record must stay immutable to consumers. In
`src/Alberto.Dcb/Configuration/AlbertoModuleDefinition.cs`, change every property accessor from
`init` to `internal set` and drop `required` from `ModuleKey`, giving it a default:

```csharp
    /// <summary>The module key passed to <c>AddAlberto</c>. Also the DI service key.</summary>
    public string ModuleKey { get; internal set; } = string.Empty;

    /// <summary>Whether <c>.WithTenancy()</c> was called.</summary>
    public bool TenancyEnabled { get; internal set; }

    /// <summary>The declared storage backend, or null when none was declared.</summary>
    public IAlbertoBackendDescriptor? Backend { get; internal set; }

    /// <summary>Control loop settings.</summary>
    public ControlLoopOptions ControlLoop { get; internal set; } = new();

    /// <summary>Telemetry settings. Only meaningful when <c>.WithTelemetry()</c> was called.</summary>
    public TelemetryOptions Telemetry { get; internal set; } = new();

    /// <summary>Checkpoint hygiene settings.</summary>
    public CheckpointOptions Checkpoints { get; internal set; } = new();

    /// <summary>Whether <c>.WithTelemetry()</c> was called.</summary>
    public bool TelemetryEnabled { get; internal set; }

    /// <summary>Every processor declared on this module.</summary>
    public ImmutableArray<ProcessorDeclaration> Processors { get; internal set; } = [];
```

Then add to `src/Alberto.Dcb/Alberto.Dcb.csproj`, inside the first `<ItemGroup>`:

```xml
<InternalsVisibleTo Include="Alberto.Dcb.Tests" />
```

> `with` expressions still work: the compiler's clone-then-assign uses the setter, and
> `internal set` keeps the type read-only for consumers outside the assembly. Tests in
> `Alberto.Dcb.Tests` construct definitions with object initializers, which is why they need
> `InternalsVisibleTo`. Update Task 2's and Task 3's tests if they used `required` positional
> initialization — `new AlbertoModuleDefinition { ModuleKey = "orders" }` still compiles.

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~Alberto.Dcb.Tests.Configuration"
```

Expected: PASS, 42 tests.

- [ ] **Step 8: Run the full suite and build**

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded`, 0 warnings other than the `[Obsolete]` notices on
`DcbModuleBuilder.Services` from Alberto's own backend packages. Those are expected and go away
in Task 12; if `TreatWarningsAsErrors` turns them into errors, suppress them at the call sites
with `#pragma warning disable CS0618` and a comment pointing at Task 12, and remove the pragmas
when `Services` is deleted.

- [ ] **Step 9: Commit**

```bash
git add src/Alberto.Dcb tests/Alberto.Dcb.Tests && git commit -m "feat(config): defer module registration and validate the definition on start"
```

---

### Task 6: Replace `ControlLoopBuilder` with `ControlLoopOptions`

`ControlLoopBuilder` is the last place where knobs live only as methods. It is deleted. Its
`Build()` body moves to an internal registration helper that reads settings from the validated
`AlbertoModuleDefinition` at resolution time — so `Alberto:Modules:orders:ControlLoop:BatchSize`
reaches the running loop. `ErrorPolicy` splits into the bindable `RetryOptions` (Task 1) plus a
separately declared `IErrorClassifier`, because a classifier is code and cannot come from JSON.

**Files:**
- Delete: `src/Alberto.Dcb/ControlLoopBuilder.cs`
- Delete: `src/Alberto.Dcb/Subscriptions/ErrorPolicy.cs`
- Create: `src/Alberto.Dcb/ControlLoopRegistration.cs`
- Modify: `src/Alberto.Dcb/Subscriptions/ConsumeMiddlewares.cs`
- Modify: `src/Alberto.Dcb/Subscriptions/BatchConsumeMiddlewares.cs`
- Modify: `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/ControlLoopConfigurationTests.cs`

**Interfaces:**
- Consumes: `ControlLoopOptions`, `RetryOptions` (Task 1); `AlbertoModuleContext`, `AlbertoModuleDefinition` (Task 2); `DcbModuleBuilder.Configure` / `.Register` (Task 5).
- Produces:
  - `WithControlLoop(this DcbModuleBuilder builder, Func<ControlLoopOptions, ControlLoopOptions>? configure = null)`
  - `AddConsumeMiddleware(this DcbModuleBuilder builder, Func<IServiceProvider, ConsumeMiddleware> factory)`
  - `AddBatchConsumeMiddleware(this DcbModuleBuilder builder, Func<IServiceProvider, BatchConsumeMiddleware> factory)`
  - `UseErrorClassifier<TClassifier>(this DcbModuleBuilder builder)` and `UseErrorClassifier(this DcbModuleBuilder builder, IErrorClassifier classifier)`
  - `ConsumeMiddlewares.RetryAndDeadLetter(RetryOptions retry, IErrorClassifier classifier, IDeadLetterStore? deadLetterStore)`
  - `BatchConsumeMiddlewares.RetryAndDeadLetter(RetryOptions retry, IErrorClassifier classifier, IDeadLetterStore? deadLetterStore)`
  - `internal static class ControlLoopRegistration` with `static void Register(AlbertoModuleContext context)`

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/ControlLoopConfigurationTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class ControlLoopConfigurationTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services, string moduleKey) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get(moduleKey);

    [Fact]
    public void WithControlLoop_transforms_the_options_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with
            {
                PollingInterval = TimeSpan.FromMilliseconds(10),
                BatchSize = 500,
            }));

        var loop = Resolve(services, "orders").ControlLoop;

        loop.PollingInterval.Should().Be(TimeSpan.FromMilliseconds(10));
        loop.BatchSize.Should().Be(500);
        loop.HeadWindowSize.Should().Be(2000, "untouched properties keep their default");
    }

    [Fact]
    public void WithControlLoop_is_implied_when_it_is_never_called()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        Resolve(services, "orders").ControlLoop.Should().Be(ControlLoopOptions.Default);
    }

    [Fact]
    public void Retry_settings_are_reachable_through_the_control_loop_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { Retry = o.Retry with { MaxRetries = 5 } }));

        Resolve(services, "orders").ControlLoop.Retry.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void Configuration_wins_over_WithControlLoop()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:ControlLoop:BatchSize"] = "7",
                ["Alberto:Modules:orders:ControlLoop:Retry:MaxRetries"] = "11",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { BatchSize = 500, Retry = o.Retry with { MaxRetries = 5 } }));

        var loop = Resolve(services, "orders").ControlLoop;

        loop.BatchSize.Should().Be(7);
        loop.Retry.MaxRetries.Should().Be(11);
    }

    [Fact]
    public void Leases_are_declared_through_the_options_record()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module
            .WithInMemory()
            .WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true, ReplicaId = "pod-1" } }));

        var leases = Resolve(services, "orders").ControlLoop.Leases;

        leases.Enabled.Should().BeTrue();
        leases.ReplicaId.Should().Be("pod-1");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ControlLoopConfigurationTests"
```

Expected: compile error — `Cannot convert lambda expression to type 'Action<ControlLoopBuilder>'`.

- [ ] **Step 3: Split `ErrorPolicy`**

Delete `src/Alberto.Dcb/Subscriptions/ErrorPolicy.cs`. Its five settings and `CalculateDelay` now
live on `RetryOptions` (Task 1); its `ErrorClassifier` property becomes a separately registered
service.

In `src/Alberto.Dcb/Subscriptions/ConsumeMiddlewares.cs`, change the `RetryAndDeadLetter` factory
signature from `(ErrorPolicy policy, IDeadLetterStore? deadLetterStore)` to:

```csharp
public static ConsumeMiddleware RetryAndDeadLetter(
    RetryOptions retry,
    IErrorClassifier classifier,
    IDeadLetterStore? deadLetterStore)
```

Add `using Alberto.Dcb.Configuration;` to the file. Inside the body, replace every `policy.`
member access with `retry.` (`MaxRetries`, `RetryDelay`, `BackoffMultiplier`, `MaxRetryDelay`,
`DeadLetterOnMaxRetries`, `CalculateDelay(...)`) and replace `policy.ErrorClassifier` with
`classifier`. Make the same three edits in
`src/Alberto.Dcb/Subscriptions/BatchConsumeMiddlewares.cs`.

- [ ] **Step 4: Create the control loop registration**

Create `src/Alberto.Dcb/ControlLoopRegistration.cs`. This is `ControlLoopBuilder.Build()` with the
captured locals replaced by a read of the validated definition, and the two duplicate-detection
blocks removed — `AlbertoModuleValidator` (Task 3) reports those at startup instead:

```csharp
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb;

/// <summary>
/// Registers the per-processor control loops, the event store head, and the dead letter retry
/// loop for one module. Every setting is read from the validated
/// <see cref="AlbertoModuleDefinition"/> at resolution time, so values supplied through
/// configuration reach the running loop.
/// </summary>
internal static class ControlLoopRegistration
{
    internal static void Register(AlbertoModuleContext context)
    {
        var services = context.Services;
        var moduleKey = context.ModuleKey;

        static ControlLoopOptions Options(IServiceProvider sp, string moduleKey) =>
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey).ControlLoop;

        static IErrorClassifier Classifier(IServiceProvider sp, string moduleKey) =>
            sp.GetKeyedService<IErrorClassifier>(moduleKey) ?? DefaultErrorClassifier.Instance;

        static IEventStoreBackend Backend(IServiceProvider sp, string moduleKey) =>
            sp.GetKeyedService<IEventStoreBackend>($"{moduleKey}:consumer")
            ?? sp.GetKeyedService<IEventStoreBackend>(moduleKey)
            ?? throw new InvalidOperationException(
                $"No event store backend is registered for Alberto module '{moduleKey}'. " +
                "Call .WithPostgres(...) or .WithInMemory() on the module builder.");

        services.AddKeyedSingleton<EventStoreHead>(moduleKey, (sp, _) =>
        {
            var options = Options(sp, moduleKey);
            var headBackend = Backend(sp, moduleKey) as IEventStoreHeadBackend
                ?? throw new InvalidOperationException(
                    $"The event store backend registered for Alberto module '{moduleKey}' does not " +
                    "implement IEventStoreHeadBackend. All built-in backends implement this interface. " +
                    "If you are using a custom backend, implement IEventStoreHeadBackend alongside " +
                    "IEventStoreBackend to enable subscriber head tracking.");

            // Optional push-wakeup — present only when a backend registers it (e.g. Postgres LISTEN/NOTIFY).
            var signal = sp.GetKeyedService<IEventAppendedSignal>(moduleKey);

            return new EventStoreHead(
                headBackend,
                options.HeadRefreshInterval,
                options.HeadWindowSize,
                sp.GetService<ILogger<EventStoreHead>>(),
                signal);
        });

        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredKeyedService<EventStoreHead>(moduleKey));

        // One ControlLoop per registered IEventProcessor.
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = Options(sp, moduleKey);
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();

            var executionOptionsByProcessorId = sp
                .GetKeyedServices<ProcessorExecutionRegistration>(moduleKey)
                .ToDictionary(r => r.ProcessorId, r => r.Options, StringComparer.Ordinal);

            var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
            var backend = Backend(sp, moduleKey);
            var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);
            var logger = sp.GetService<ILogger<ControlLoop>>();

            // Compose the middleware chain (outermost first):
            //   [keyed ConsumeMiddleware...]   ← WithTelemetry(), AddConsumeMiddleware(...)
            //   RetryAndDeadLetter             ← always innermost
            //   processor.ProcessEventAsync    ← terminal
            var diMiddlewares = sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList();
            var diBatchMiddlewares = sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey).ToList();
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey);
            var classifier = Classifier(sp, moduleKey);

            var middlewares = new List<ConsumeMiddleware>(diMiddlewares)
            {
                ConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            var batchMiddlewares = new List<BatchConsumeMiddleware>(diBatchMiddlewares)
            {
                BatchConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            // A per-event middleware with no batch counterpart cannot be honoured on the batch
            // path, so batching falls back to per-event dispatch rather than silently skipping it.
            var hasUnpairedPerEventMiddlewares = diMiddlewares.Count > diBatchMiddlewares.Count;

            var loops = processors
                .Select(p => new ControlLoop(p, head, backend, checkpoints,
                    options.PollingInterval, options.BatchSize, moduleKey, middlewares, batchMiddlewares,
                    hasUnpairedPerEventMiddlewares,
                    executionOptionsByProcessorId.GetValueOrDefault(
                        p.ProcessorId,
                        ProcessorExecutionOptions.Default),
                    logger))
                .ToList();

            if (!options.Leases.Enabled)
                return new ControlLoopGroup(loops);

            var replicaId = options.Leases.ReplicaId ?? Environment.MachineName;
            var leaseManager = sp.GetRequiredKeyedService<IProcessorLeaseManager>(moduleKey);

            // Enable fenced checkpoint writes and wire the fence-violation callback through
            // IFencableCheckpointStore so we don't downcast to the concrete type. Any wrapper
            // around CachingCheckpointStore that also implements IFencableCheckpointStore is
            // reached correctly; wrapping without implementing it still compiles — the fencing
            // block is simply skipped — which is visible to the integrator rather than silently
            // dropping the callback.
            LeaseAwareControlLoopGroup? leaseGroup = null;
            if (checkpoints is IFencableCheckpointStore fencable)
            {
                fencable.SetFencingContext(
                    new FencingContext(moduleKey, replicaId, UseProcessorLeaseFencing: true));

                // Wired BEFORE the group is constructed so the variable is captured by reference
                // and is set by the time the lambda first runs (from a periodic timer).
                fencable.OnFenceViolation = _ =>
                {
                    // Fire-and-forget: cancel the group so the fenced-out replica stops
                    // dispatching duplicate side effects. The callback is synchronous;
                    // discarding the Task is intentional.
                    _ = leaseGroup?.StopAsync(CancellationToken.None);
                };
            }

            leaseGroup = new LeaseAwareControlLoopGroup(
                loops, leaseManager, moduleKey, replicaId,
                sp.GetService<ILogger<LeaseAwareControlLoopGroup>>());

            return leaseGroup;
        });

        // Dead letter retry loop — dedicated polling for CLI-requested retries.
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = Options(sp, moduleKey);
            var processors = sp.GetKeyedServices<IEventProcessor>(moduleKey).ToList();
            var deadLetterStore = sp.GetKeyedService<IDeadLetterStore>(moduleKey)
                ?? throw new InvalidOperationException(
                    $"No IDeadLetterStore registered for module '{moduleKey}'. " +
                    "The dead letter retry loop requires a dead letter store.");

            var classifier = Classifier(sp, moduleKey);
            var logger = sp.GetService<ILogger<DeadLetterRetryLoop>>();

            var middlewares = new List<ConsumeMiddleware>(sp.GetKeyedServices<ConsumeMiddleware>(moduleKey))
            {
                ConsumeMiddlewares.RetryAndDeadLetter(options.Retry, classifier, deadLetterStore),
            };

            var replicaId = options.Leases.ReplicaId ?? Environment.MachineName;

            var retryLoops = processors
                .Select(p => new DeadLetterRetryLoop(
                    p,
                    deadLetterStore,
                    options.DeadLetterRetry.PollingInterval,
                    options.DeadLetterRetry.BatchSize,
                    middlewares,
                    logger,
                    options.DeadLetterRetry.ClaimLease,
                    replicaId))
                .ToList();

            return new DeadLetterRetryLoopGroup(retryLoops);
        });
    }
}
```

Then delete `src/Alberto.Dcb/ControlLoopBuilder.cs`.

- [ ] **Step 5: Replace `WithControlLoop` and add the middleware/classifier entry points**

In `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs`, replace the existing `WithControlLoop` method
with the following four methods (add `using Alberto.Dcb.Configuration;` at the top):

```csharp
    /// <summary>
    /// Configures the async control loop. Called implicitly with defaults when omitted.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">
    /// Transforms the current options. Use a <c>with</c> expression:
    /// <c>o => o with { BatchSize = 500 }</c>. Anything set here is still overridable from
    /// <c>Alberto:Modules:{moduleKey}:ControlLoop</c>.
    /// </param>
    public static DcbModuleBuilder WithControlLoop(
        this DcbModuleBuilder builder,
        Func<ControlLoopOptions, ControlLoopOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Configure(d => d with
            {
                ControlLoop = configure(d.ControlLoop)
                    ?? throw new InvalidOperationException("WithControlLoop configurator returned null."),
            });
        }

        if (!builder.ControlLoopConfigured)
        {
            builder.ControlLoopConfigured = true;
            builder.Register(ControlLoopRegistration.Register);
        }

        return builder;
    }

    /// <summary>
    /// Adds a middleware to the per-event consume pipeline. Middlewares run in registration order
    /// (first added is outermost). The built-in retry-and-dead-letter middleware is always the
    /// innermost layer, so custom middleware observes the outcome of the whole retry sequence.
    /// </summary>
    public static DcbModuleBuilder AddConsumeMiddleware(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, ConsumeMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, (sp, _) => factory(sp)));
    }

    /// <summary>
    /// Adds a middleware to the batch consume pipeline. A per-event middleware without a batch
    /// counterpart forces the control loop back onto per-event dispatch, so register both when a
    /// processor should keep batching.
    /// </summary>
    public static DcbModuleBuilder AddBatchConsumeMiddleware(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, BatchConsumeMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, (sp, _) => factory(sp)));
    }

    /// <summary>
    /// Replaces the classifier that decides whether a handler failure is transient (retry) or
    /// permanent (dead-letter immediately). Defaults to <see cref="DefaultErrorClassifier"/>.
    /// </summary>
    public static DcbModuleBuilder UseErrorClassifier(
        this DcbModuleBuilder builder,
        IErrorClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(classifier);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, classifier));
    }

    /// <summary>
    /// Replaces the error classifier with one resolved from the container, so it can take
    /// dependencies. Defaults to <see cref="DefaultErrorClassifier"/>.
    /// </summary>
    public static DcbModuleBuilder UseErrorClassifier<TClassifier>(this DcbModuleBuilder builder)
        where TClassifier : class, IErrorClassifier
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton<IErrorClassifier, TClassifier>(context.ModuleKey));
    }
```

- [ ] **Step 6: Run the control loop tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ControlLoopConfigurationTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 7: Fix the fallout across the solution**

```bash
dotnet build 2>&1 | grep -E "error|warning" | sort -u
```

Every remaining error is a call site that used a deleted method. Apply these mappings:

| Old | New |
|---|---|
| `.WithPollingInterval(x)` | `o => o with { PollingInterval = x }` |
| `.WithBatchSize(n)` | `o => o with { BatchSize = n }` |
| `.WithHeadRefreshInterval(x)` | `o => o with { HeadRefreshInterval = x }` |
| `.WithRetryLoopPollingInterval(x)` | `o => o with { DeadLetterRetry = o.DeadLetterRetry with { PollingInterval = x } }` |
| `.WithRetryLoopBatchSize(n)` | `o => o with { DeadLetterRetry = o.DeadLetterRetry with { BatchSize = n } }` |
| `.WithRetryLoopClaimLease(x)` | `o => o with { DeadLetterRetry = o.DeadLetterRetry with { ClaimLease = x } }` |
| `.WithProcessorLeases(id)` | `o => o with { Leases = o.Leases with { Enabled = true, ReplicaId = id } }` |
| `.WithErrorPolicy(p => p with { MaxRetries = n })` | `o => o with { Retry = o.Retry with { MaxRetries = n } }` |
| `.WithErrorPolicy(new ErrorPolicy { ErrorClassifier = c })` | `.UseErrorClassifier(c)` |
| `loop.WithMiddleware(m)` | `module.AddConsumeMiddleware(_ => m)` |
| `loop.WithBatchMiddleware(m)` | `module.AddBatchConsumeMiddleware(_ => m)` |
| `ErrorPolicy.Default` | `new RetryOptions()` plus `DefaultErrorClassifier.Instance` |

Repeat the build-and-fix loop until it is clean.

- [ ] **Step 8: Run the full suite**

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded`, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A src tests apps tools && git commit -m "feat(config)!: replace ControlLoopBuilder and ErrorPolicy with bindable options records"
```

---

### Task 7: Processor registration — derived ids, record-based execution options, declaration

Three changes to `DcbModuleBuilderExtensions`, all in the same file and all affecting the same
call sites, so they land together:

1. `Action<ProcessorExecutionConfigurator>` becomes `Func<ProcessorExecutionOptions, ProcessorExecutionOptions>` — the third and last mutable-builder idiom disappears, and `ProcessorExecutionConfigurator` is deleted.
2. The `ReactTo<TEvent, THandler>` overloads derive their processor id from `THandler` via `ProcessorId.For<THandler>()` (Task 4); `processorId` becomes an optional override. The lambda overloads keep a required `processorId` because there is no type to derive from.
3. Every registration also calls `builder.DeclareProcessor(...)` so `AlbertoModuleValidator` can see it.

**Files:**
- Modify: `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs`
- Modify: `src/Alberto.Dcb/Subscriptions/ProcessorExecutionOptions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/ProcessorRegistrationTests.cs`

**Interfaces:**
- Consumes: `ProcessorId.For<T>()` (Task 4); `DcbModuleBuilder.DeclareProcessor` (Task 5); `ProcessorDeclaration`, `ProcessorKind` (Task 2).
- Produces:
  - `AddProjection<TState>(this DcbModuleBuilder builder, ProjectionDeclaration<TState> declaration, Func<IServiceProvider, Func<IStateStore<TState>>> stateStoreFactory)` — signature unchanged, now also declares the processor
  - `ReactTo<TEvent>(this DcbModuleBuilder builder, Func<IServiceProvider, Func<TEvent, CancellationToken, Task>> handlerFactory, string processorId, ReactorMode mode = ReactorMode.Async, Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)`
  - the `ReactorContext` sibling of the above, same shape
  - `ReactTo<TEvent, THandler>(this DcbModuleBuilder builder, Func<THandler, Func<TEvent, CancellationToken, Task>> methodSelector, string? processorId = null, ReactorMode mode = ReactorMode.Async, Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)`
  - the `ReactorContext` sibling of the above, same shape

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/ProcessorRegistrationTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class ProcessorRegistrationTests
{
    private sealed record ShipmentDispatched(string Id) : IEvent;

    private sealed class ShipmentNotifier
    {
        public Task HandleAsync(ShipmentDispatched e, CancellationToken ct) => Task.CompletedTask;
    }

    [ProcessorId("shipments.legacy")]
    private sealed class RenamedNotifier
    {
        public Task HandleAsync(ShipmentDispatched e, CancellationToken ct) => Task.CompletedTask;
    }

    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    private static IServiceCollection Module(Action<DcbModuleBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ShipmentNotifier>();
        services.AddSingleton<RenamedNotifier>();
        services.AddAlberto("orders", module =>
        {
            module.WithInMemory();
            configure(module);
        });
        return services;
    }

    [Fact]
    public void A_handler_based_reactor_derives_its_processor_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("ShipmentNotifier");
    }

    [Fact]
    public void The_ProcessorId_attribute_overrides_the_derived_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, RenamedNotifier>(h => h.HandleAsync));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("shipments.legacy");
    }

    [Fact]
    public void An_explicit_processor_id_still_wins()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(
            h => h.HandleAsync, processorId: "explicit"));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.ProcessorId.Should().Be("explicit");
    }

    [Fact]
    public void A_declared_processor_records_its_handler_type()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync));

        Resolve(services).Processors[0].HandlerType.Should().Be<ShipmentNotifier>();
    }

    [Fact]
    public void Execution_options_are_configured_with_a_with_expression()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched, ShipmentNotifier>(
            h => h.HandleAsync,
            configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported, MaxConcurrency = 4 }));

        var execution = Resolve(services).Processors[0].Execution;

        execution.BatchingMode.Should().Be(ProcessorBatchingMode.IfSupported);
        execution.MaxConcurrency.Should().Be(4);
    }

    [Fact]
    public void Two_reactors_on_the_same_handler_type_are_reported_as_a_duplicate_id()
    {
        var services = Module(m => m
            .ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync)
            .ReactTo<ShipmentDispatched, ShipmentNotifier>(h => h.HandleAsync));

        var failures = new AlbertoModuleValidator().Collect(Resolve(services));

        failures.Should().Contain(f => f.Code == "ALB0002");
        failures.Single(f => f.Code == "ALB0002").Problem.Should().Contain("ShipmentNotifier");
    }

    [Fact]
    public void A_lambda_reactor_still_requires_an_explicit_processor_id()
    {
        var services = Module(m => m.ReactTo<ShipmentDispatched>(
            _ => (_, _) => Task.CompletedTask,
            processorId: "shipment-lambda"));

        Resolve(services).Processors.Should().ContainSingle()
            .Which.HandlerType.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ProcessorRegistrationTests"
```

Expected: compile errors — `no overload takes 1 argument` on the `ReactTo<TEvent, THandler>` calls.

- [ ] **Step 3: Delete `ProcessorExecutionConfigurator`**

In `src/Alberto.Dcb/Subscriptions/ProcessorExecutionOptions.cs`, delete the entire
`ProcessorExecutionConfigurator` class (lines 35–95). Keep `ProcessorBatchingMode`,
`ProcessorExecutionOptions` and `ProcessorExecutionRegistration` exactly as they are. The
"concurrency requires batching" rule it enforced is now `ALB0005` in `AlbertoModuleValidator`,
which reports it at startup alongside every other problem instead of throwing from a builder.

- [ ] **Step 4: Replace the execution-options helper**

In `src/Alberto.Dcb/DcbModuleBuilderExtensions.cs`, replace `BuildProcessorExecutionOptions`:

```csharp
    private static ProcessorExecutionOptions BuildProcessorExecutionOptions(
        ReactorMode mode,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure)
    {
        var options = mode == ReactorMode.Sync
            ? SyncExecutionDefault
            : ProcessorExecutionOptions.Default;

        if (configure is null)
            return options;

        return configure(options)
            ?? throw new InvalidOperationException("Processor execution configurator returned null.");
    }
```

Every existing call reads `BuildProcessorExecutionOptions(configure)`; change each to
`BuildProcessorExecutionOptions(mode, configure)` and delete the now-redundant branch that
selected `SyncExecutionDefault` at the call site if one exists. Leave
`ValidateSyncExecutionOptions` as it is — it guards a genuinely impossible combination
(`ReactorMode.Sync` with async batching) that the caller can only reach by passing both.

- [ ] **Step 5: Change the four `ReactTo` signatures and declare the processors**

In the same file, for **each** of the four `ReactTo` overloads:

1. Change the `configure` parameter type from `Action<ProcessorExecutionConfigurator>? configure = null` to `Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null`.
2. On the two `ReactTo<TEvent, THandler>` overloads, change `string processorId` to `string? processorId = null` and move it after `methodSelector`, then resolve it as the first statement of the body:

```csharp
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(methodSelector);

        var resolvedProcessorId = processorId ?? ProcessorId.For<THandler>();
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedProcessorId);
```

   Then replace every subsequent use of `processorId` in that method body with `resolvedProcessorId`.
3. Immediately after `var executionOptions = BuildProcessorExecutionOptions(mode, configure);`, add the declaration. For the two `THandler` overloads:

```csharp
        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = resolvedProcessorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
            HandlerType = typeof(THandler),
        });
```

   For the two lambda overloads (which have no handler type):

```csharp
        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = processorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
        });
```

- [ ] **Step 6: Declare projections too**

In `AddProjection<TState>`, immediately after the argument-null checks, add:

```csharp
        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });
```

> `ProjectionDeclaration<TState>` already carries the processor id that the registered
> `IEventProcessor` reports. If the property is named differently in
> `src/Alberto.Dcb/Projections/ProjectionDeclaration.cs`, use that name — do not invent a second
> source of truth for the id.

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~ProcessorRegistrationTests"
```

Expected: PASS, 7 tests.

- [ ] **Step 8: Fix the fallout and run everything**

```bash
dotnet build 2>&1 | grep -E "error" | sort -u
```

Call sites using `configure: c => c.BatchIfSupported()` become
`configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported }`;
`c.RequireBatching()` becomes `ProcessorBatchingMode.Required`; `c.DisableBatching()` becomes
`ProcessorBatchingMode.Disabled`; `c.WithConcurrency(n)` becomes `MaxConcurrency = n`. Then:

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded`, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A src tests apps && git commit -m "feat(config)!: derive processor ids and configure execution with record updates"
```

---

### Task 8: Postgres backend descriptor and startup migrations

The two worst offences live here: `WithPostgres` runs DbUp migrations and opens a validation
connection *during DI composition*, and it reads `builder.HasTenancy` at call time, which is why
`TenancyOrderingValidator` exists to detect a mis-ordered chain after the fact. Both go away.

**Files:**
- Modify: `src/Alberto.Dcb.Postgres/PostgresOptions.cs`
- Create: `src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs`
- Create: `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs`
- Modify: `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/PostgresDescriptorTests.cs`

**Interfaces:**
- Consumes: `IAlbertoBackendDescriptor`, `AlbertoModuleContext`, `AlbertoModuleDefinition` (Task 2); `AlbertoValidationFailure` (Task 3); `IAlbertoOverrides<T>`, `AlbertoOptionsOverlay` (Task 1); `DcbModuleBuilder.UseBackend` (Task 5).
- Produces:
  - `PostgresOptions` as a record with `PostgresOverrides` mirror
  - `PostgresBackendDescriptor : IAlbertoBackendDescriptor` with `PostgresOptions Options { get; init; }`, `Name => "Postgres"`, `SupportsTenancy => true`
  - `AlbertoMigrationHostedService : IHostedService`
  - `WithPostgres(this DcbModuleBuilder builder, Func<PostgresOptions, PostgresOptions> configure)`
- Postgres validation codes: `ALB1001` empty connection string, `ALB1002` non-positive `MaxPoolSize`, `ALB1003` `MinPoolSize` greater than `MaxPoolSize`, `ALB1004` non-positive `LeaseDuration`.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/PostgresDescriptorTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class PostgresDescriptorTests
{
    private const string ConnectionString = "Host=localhost;Database=alberto;Username=x;Password=y";

    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    private static PostgresOptions OptionsOf(AlbertoModuleDefinition definition) =>
        definition.Backend.Should().BeOfType<PostgresBackendDescriptor>().Subject.Options;

    [Fact]
    public void WithPostgres_declares_the_backend_without_connecting()
    {
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString, Schema = "orders" }));

        var options = OptionsOf(Resolve(services));
        options.ConnectionString.Should().Be(ConnectionString);
        options.Schema.Should().Be("orders");
    }

    [Fact]
    public void Tenancy_declared_after_the_backend_still_reaches_the_backend()
    {
        var services = new ServiceCollection();

        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString })
            .WithTenancy());

        Resolve(services).TenancyEnabled.Should().BeTrue();
    }

    [Fact]
    public void Postgres_options_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:MaxPoolSize"] = "77",
                ["Alberto:Modules:orders:Postgres:AutoMigrate"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module
            .WithPostgres(o => o with { ConnectionString = ConnectionString, MaxPoolSize = 30 }));

        var options = OptionsOf(Resolve(services));
        options.MaxPoolSize.Should().Be(77);
        options.AutoMigrate.Should().BeFalse();
    }

    [Fact]
    public void A_connection_string_supplied_only_by_configuration_is_accepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Postgres:ConnectionString"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module.WithPostgres(o => o));

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().NotContain(f => f.Code == "ALB1001");
    }

    [Fact]
    public void An_empty_connection_string_fails_with_ALB1001()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithPostgres(o => o));

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().Contain(f => f.Code == "ALB1001");
    }

    [Fact]
    public void An_inverted_pool_range_fails_with_ALB1003()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithPostgres(o => o with
        {
            ConnectionString = ConnectionString,
            MinPoolSize = 50,
            MaxPoolSize = 10,
        }));

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().Contain(f => f.Code == "ALB1003");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~PostgresDescriptorTests"
```

Expected: compile error — `The name 'PostgresBackendDescriptor' does not exist`.

- [ ] **Step 3: Convert `PostgresOptions` to a record and add its mirror**

Replace the contents of `src/Alberto.Dcb.Postgres/PostgresOptions.cs`, keeping the existing XML
doc comments on each property:

```csharp
using Alberto.Dcb.Configuration;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Settings for the PostgreSQL event store backend.
/// </summary>
public sealed record PostgresOptions
{
    /// <summary>The Npgsql connection string. Required.</summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>Whether Alberto applies its DbUp migrations at startup. Default true.</summary>
    public bool AutoMigrate { get; init; } = true;

    /// <summary>The schema Alberto's tables live in. Null means the connection's default schema.</summary>
    public string? Schema { get; init; }

    /// <summary>Maximum Npgsql pool size. Default 100.</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>Minimum Npgsql pool size. Default 0.</summary>
    public int MinPoolSize { get; init; }

    /// <summary>How long a processor lease is held before it can be stolen. Default 60 seconds.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether consumers wait behind the stable-head visibility barrier. Default true.</summary>
    public bool EnableStableHeadBarrier { get; init; } = true;

    /// <summary>Whether the LISTEN/NOTIFY push-wakeup listener runs. Default true.</summary>
    public bool EnableNotifyListener { get; init; } = true;
}

/// <summary>Configuration mirror for <see cref="PostgresOptions"/>.</summary>
public sealed class PostgresOverrides : IAlbertoOverrides<PostgresOptions>
{
    public string? ConnectionString { get; set; }
    public bool? AutoMigrate { get; set; }
    public string? Schema { get; set; }
    public int? MaxPoolSize { get; set; }
    public int? MinPoolSize { get; set; }
    public TimeSpan? LeaseDuration { get; set; }
    public bool? EnableStableHeadBarrier { get; set; }
    public bool? EnableNotifyListener { get; set; }

    /// <inheritdoc />
    public PostgresOptions ApplyTo(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            ConnectionString = ConnectionString ?? options.ConnectionString,
            AutoMigrate = AutoMigrate ?? options.AutoMigrate,
            Schema = Schema ?? options.Schema,
            MaxPoolSize = MaxPoolSize ?? options.MaxPoolSize,
            MinPoolSize = MinPoolSize ?? options.MinPoolSize,
            LeaseDuration = LeaseDuration ?? options.LeaseDuration,
            EnableStableHeadBarrier = EnableStableHeadBarrier ?? options.EnableStableHeadBarrier,
            EnableNotifyListener = EnableNotifyListener ?? options.EnableNotifyListener,
        };
    }
}
```

Then extend the parity test in `tests/Alberto.Dcb.Tests/Configuration/OptionsOverrideParityTests.cs`
so it also scans the Postgres assembly — change `DiscoverPairs`'s `assemblies` array to:

```csharp
        var assemblies = new[]
        {
            typeof(ControlLoopOptions).Assembly,
            typeof(Alberto.Dcb.Postgres.PostgresOptions).Assembly,
        };
```

- [ ] **Step 4: Create the migration hosted service**

Create `src/Alberto.Dcb.Postgres/AlbertoMigrationHostedService.cs`. This is the code that used to
run inline inside `WithPostgres`:

```csharp
using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Applies Alberto's schema migrations and checks the tenancy mode of the existing schema.
/// This runs at startup rather than during service registration, so building a
/// <see cref="IServiceProvider"/> — in a test, a design-time factory, or a CLI tool — never
/// opens a database connection.
/// </summary>
internal sealed class AlbertoMigrationHostedService(
    string moduleKey,
    IOptionsMonitor<AlbertoModuleDefinition> definitions,
    ILogger<AlbertoMigrationHostedService>? logger = null) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var definition = definitions.Get(moduleKey);

        if (definition.Backend is not PostgresBackendDescriptor descriptor)
            return Task.CompletedTask;

        var options = descriptor.Options;

        if (options.AutoMigrate)
        {
            logger?.LogInformation(
                "Applying Alberto migrations for module {ModuleKey} to schema {Schema}.",
                moduleKey, options.Schema ?? "(default)");

            PostgresMigrator.Migrate(options.ConnectionString, options.Schema);
        }

        // Catches the case where a schema was created single-tenant and the module is now
        // declared .WithTenancy() (or the reverse) — the tables differ and the mismatch would
        // otherwise surface as a confusing missing-column error on the first append.
        PostgresMigrator.ValidateTenancyMode(
            options.ConnectionString,
            options.Schema,
            singleTenant: !definition.TenancyEnabled);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

> **Ordering:** `IHost` starts hosted services in registration order. `AlbertoMigrationHostedService`
> is registered by `PostgresBackendDescriptor.Register`, which `AddAlberto` calls before running
> the deferred registrations that add the control loop, so migrations complete before any
> consumer starts. Do not enable `ServicesStartConcurrently`.

- [ ] **Step 5: Create the descriptor**

Create `src/Alberto.Dcb.Postgres/PostgresBackendDescriptor.cs`:

```csharp
using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Declares the PostgreSQL event store backend for a module.
/// </summary>
/// <param name="Options">The backend's settings, before any configuration overlay.</param>
public sealed record PostgresBackendDescriptor(PostgresOptions Options) : IAlbertoBackendDescriptor
{
    /// <inheritdoc />
    public string Name => "Postgres";

    /// <inheritdoc />
    public bool SupportsTenancy => true;

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) =>
        this with
        {
            Options = AlbertoOptionsOverlay.Overlay<PostgresOptions, PostgresOverrides>(
                moduleSection, "Postgres", Options),
        };

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition)
    {
        var path = $"{definition.ConfigurationPath}:Postgres";

        if (string.IsNullOrWhiteSpace(Options.ConnectionString))
        {
            yield return new AlbertoValidationFailure(
                "ALB1001",
                "The Postgres backend has no connection string.",
                $"Set it with .WithPostgres(o => o with {{ ConnectionString = ... }}) or '{path}:ConnectionString'.");
        }

        if (Options.MaxPoolSize <= 0)
        {
            yield return new AlbertoValidationFailure(
                "ALB1002",
                $"Postgres MaxPoolSize is {Options.MaxPoolSize}, which is not a positive count.",
                $"Set a positive pool size via '{path}:MaxPoolSize'.");
        }

        if (Options.MinPoolSize > Options.MaxPoolSize)
        {
            yield return new AlbertoValidationFailure(
                "ALB1003",
                $"Postgres MinPoolSize ({Options.MinPoolSize}) is larger than MaxPoolSize ({Options.MaxPoolSize}).",
                $"Lower '{path}:MinPoolSize' or raise '{path}:MaxPoolSize'.");
        }

        if (Options.LeaseDuration <= TimeSpan.Zero)
        {
            yield return new AlbertoValidationFailure(
                "ALB1004",
                $"Postgres LeaseDuration is {Options.LeaseDuration}, which is not a positive duration.",
                $"Set a positive duration via '{path}:LeaseDuration'.");
        }
    }

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var moduleKey = context.ModuleKey;

        services.AddSingleton<IHostedService>(sp => new AlbertoMigrationHostedService(
            moduleKey,
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>(),
            sp.GetService<ILogger<AlbertoMigrationHostedService>>()));

        if (context.TenancyEnabled)
            PostgresBuilderExtensions.RegisterTenantBackend(context, Options);
        else
            PostgresBuilderExtensions.RegisterSingleTenantBackend(context, Options);
    }
}
```

- [ ] **Step 6: Rewrite `WithPostgres` and delete the ordering validator**

In `src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs`:

1. Replace the public `WithPostgres` method with:

```csharp
    /// <summary>
    /// Uses PostgreSQL as this module's event store.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">
    /// Transforms the default options. Use a <c>with</c> expression:
    /// <c>o => o with { ConnectionString = cs, Schema = "orders" }</c>. Every property is also
    /// settable from <c>Alberto:Modules:{moduleKey}:Postgres</c>, which wins over this callback.
    /// </param>
    /// <remarks>
    /// This declares the backend. No connection is opened and no migration runs until the host
    /// starts, so building a service provider is always side-effect free.
    /// </remarks>
    public static DcbModuleBuilder WithPostgres(
        this DcbModuleBuilder builder,
        Func<PostgresOptions, PostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = configure(new PostgresOptions())
            ?? throw new InvalidOperationException("WithPostgres configurator returned null.");

        return builder.UseBackend(new PostgresBackendDescriptor(options));
    }
```

2. Delete the `TenancyOrderingValidator` file-local class entirely, along with its registration.
   The hazard it detected — reading `HasTenancy` before `.WithTenancy()` was called — is gone,
   because `Register` runs after the whole lambda.

3. Change `RegisterSingleTenantBackend` and `RegisterTenantBackend` from private to
   `internal static`, and change their first parameter from
   `(DcbModuleBuilder builder, PostgresOptions options)` to
   `(AlbertoModuleContext context, PostgresOptions options)`. Inside both methods, and inside
   `RegisterInlineProjections` and `RegisterPostAppendHandlers` if they take the builder too,
   replace `builder.Services` with `context.Services` and `builder.ModuleKey` with
   `context.ModuleKey`. Nothing else in those bodies changes — the keyed registrations
   (`moduleKey`, `$"{moduleKey}:tenant-raw"`, `$"{moduleKey}:consumer"`) stay exactly as they are.

4. Remove the inline `PostgresMigrator.Migrate(...)` and `PostgresMigrator.ValidateTenancyMode(...)`
   calls — they now live in `AlbertoMigrationHostedService`.

- [ ] **Step 7: Run the tests**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~PostgresDescriptorTests|FullyQualifiedName~OptionsOverrideParityTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 8: Build, fix call sites, run everything**

```bash
dotnet build 2>&1 | grep -E "error" | sort -u
```

Every `.WithPostgres(options => { options.X = y; ... })` becomes
`.WithPostgres(o => o with { X = y, ... })`. Then:

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded`, all tests pass. Postgres integration tests that relied on
`AddAlberto` having already migrated the schema now need the host started; where a test builds a
bare `ServiceProvider`, call `PostgresMigrator.Migrate(connectionString, schema)` directly in the
fixture's setup.

- [ ] **Step 9: Commit**

```bash
git add -A src tests apps && git commit -m "feat(postgres)!: declare the backend as a descriptor and migrate at startup"
```

---

### Task 9: In-memory backend descriptor and Entity Framework deferred registration

The in-memory backend becomes a descriptor so `.WithInMemory()` satisfies `ALB0001` and so tests
exercise the same declaration path as production. Entity Framework keeps its own
`Action<DbContextOptionsBuilder>` idiom verbatim — that is EF's API, not Alberto's, and mirroring
it is what "use the standard ways of working of .NET" means here — but its registration moves
behind `builder.Register(...)` so it no longer depends on chain order.

**Files:**
- Create: `src/Alberto.Dcb.InMemory/InMemoryBackendDescriptor.cs`
- Modify: `src/Alberto.Dcb.InMemory/InMemoryBuilderExtensions.cs`
- Modify: `src/Alberto.Dcb.EntityFramework/EfBuilderExtensions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/InMemoryDescriptorTests.cs`

**Interfaces:**
- Consumes: `IAlbertoBackendDescriptor`, `AlbertoModuleContext` (Task 2); `DcbModuleBuilder.UseBackend` / `.Register` (Task 5).
- Produces:
  - `InMemoryBackendDescriptor : IAlbertoBackendDescriptor` with `Name => "InMemory"`, `SupportsTenancy => false`, and `string? SharedModuleKey { get; init; }`
  - `WithInMemory(this DcbModuleBuilder builder)` and `WithInMemory(this DcbModuleBuilder builder, string sharedModuleKey)` — signatures unchanged
  - `WithEntityFramework<TDbContext>` — signatures unchanged

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/InMemoryDescriptorTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Tests.Configuration;

public class InMemoryDescriptorTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    [Fact]
    public void WithInMemory_declares_the_backend()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        Resolve(services).Backend.Should().BeOfType<InMemoryBackendDescriptor>();
    }

    [Fact]
    public void WithInMemory_satisfies_the_backend_requirement()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory());

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().NotContain(f => f.Code == "ALB0001");
    }

    [Fact]
    public void The_in_memory_backend_does_not_support_tenancy()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTenancy());

        new AlbertoModuleValidator()
            .Collect(Resolve(services))
            .Should().Contain(f => f.Code == "ALB0003");
    }

    [Fact]
    public void A_shared_module_key_is_recorded_on_the_descriptor()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory("shared"));

        Resolve(services).Backend.Should().BeOfType<InMemoryBackendDescriptor>()
            .Which.SharedModuleKey.Should().Be("shared");
    }

    [Fact]
    public async Task An_in_memory_module_starts_and_stops_cleanly()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddAlberto("orders", module => module.WithInMemory());
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~InMemoryDescriptorTests"
```

Expected: compile error — `The name 'InMemoryBackendDescriptor' does not exist`.

- [ ] **Step 3: Create the in-memory descriptor**

Create `src/Alberto.Dcb.InMemory/InMemoryBackendDescriptor.cs`:

```csharp
using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Configuration;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// Declares the in-memory event store backend. Intended for tests, samples and local
/// development: state lives for the lifetime of the process and nothing is durable.
/// </summary>
public sealed record InMemoryBackendDescriptor : IAlbertoBackendDescriptor
{
    /// <summary>
    /// When set, this module reads and writes another module's in-memory store instead of its
    /// own, so several modules can share one event log in a test.
    /// </summary>
    public string? SharedModuleKey { get; init; }

    /// <inheritdoc />
    public string Name => "InMemory";

    /// <inheritdoc />
    public bool SupportsTenancy => false;

    /// <inheritdoc />
    public IAlbertoBackendDescriptor ApplyConfiguration(IConfiguration moduleSection) => this;

    /// <inheritdoc />
    public IEnumerable<AlbertoValidationFailure> Validate(AlbertoModuleDefinition definition) => [];

    /// <inheritdoc />
    public void Register(AlbertoModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        InMemoryBuilderExtensions.RegisterBackend(context, SharedModuleKey);
    }
}
```

- [ ] **Step 4: Rewrite the in-memory builder extensions**

In `src/Alberto.Dcb.InMemory/InMemoryBuilderExtensions.cs`, replace both `WithInMemory` overloads
with declarations, and move the existing registration bodies into one internal helper:

```csharp
    /// <summary>
    /// Uses an in-process event store for this module. Nothing is durable; use it for tests,
    /// samples and local development.
    /// </summary>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.UseBackend(new InMemoryBackendDescriptor());
    }

    /// <summary>
    /// Uses the in-process event store belonging to <paramref name="sharedModuleKey"/>, so
    /// several modules observe one event log. Useful when a test spans two modules.
    /// </summary>
    public static DcbModuleBuilder WithInMemory(this DcbModuleBuilder builder, string sharedModuleKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedModuleKey);

        return builder.UseBackend(new InMemoryBackendDescriptor { SharedModuleKey = sharedModuleKey });
    }

    /// <summary>
    /// Registers the in-memory backend's services. Called by
    /// <see cref="InMemoryBackendDescriptor.Register"/> once the declaration is final.
    /// </summary>
    internal static void RegisterBackend(AlbertoModuleContext context, string? sharedModuleKey)
    {
        // Move the bodies of the two former WithInMemory overloads here verbatim, replacing
        // `builder.Services` with `context.Services` and `builder.ModuleKey` with
        // `context.ModuleKey`. Where the shared overload resolved services keyed by
        // sharedModuleKey, branch on `sharedModuleKey is not null` and keep the same keys:
        //   IAppendInterceptorPipeline, IEventStoreBackend, IEventStore, ICheckpointStore,
        //   IDeadLetterStore — all keyed by context.ModuleKey.
    }
```

> The comment block above is the one place in this plan that describes a move rather than showing
> the code: the bodies are long, unchanged, and already in the file. Cut and paste them; change
> only the two identifiers named. Add `using Alberto.Dcb.Configuration;` to the file.

- [ ] **Step 5: Defer the Entity Framework registration**

In `src/Alberto.Dcb.EntityFramework/EfBuilderExtensions.cs`, wrap both `WithEntityFramework<TDbContext>`
bodies in `builder.Register(...)`. The `Action<DbContextOptionsBuilder>` parameter stays — it is
EF's own idiom and callers already know it:

```csharp
    /// <summary>
    /// Registers a pooled <typeparamref name="TDbContext"/> factory for this module's
    /// EF-backed projections. Takes EF's own options builder unchanged.
    /// </summary>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<DbContextOptionsBuilder> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Register(context =>
            context.Services.AddPooledDbContextFactory<TDbContext>(configure));
    }

    /// <summary>
    /// Registers a pooled <typeparamref name="TDbContext"/> factory whose options depend on
    /// other services.
    /// </summary>
    public static DcbModuleBuilder WithEntityFramework<TDbContext>(
        this DcbModuleBuilder builder,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Register(context =>
            context.Services.AddPooledDbContextFactory<TDbContext>(configure));
    }
```

Add `using Alberto.Dcb.Configuration;`. Apply the same `builder.Register(context => ...)` wrapping
to `AddEfProjection<TEntity, TDbContext>` in the same file, replacing `builder.Services` with
`context.Services`, and add a `builder.DeclareProcessor(...)` call outside the `Register` callback
using the declaration's processor id and `ProcessorKind.Projection`, matching Task 7 Step 6.

- [ ] **Step 6: Run the tests and the suite**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~InMemoryDescriptorTests" && dotnet build && dotnet test
```

Expected: PASS, 5 new tests; `Build succeeded`; all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A src tests && git commit -m "feat(config)!: declare in-memory and EF backends through deferred registration"
```

---

### Task 10: Telemetry registers its own OpenTelemetry sources

Today `.WithTelemetry()` wires Alberto's interceptors, and the application separately has to call
`AddAlbertoInstrumentation()` on both the tracer and meter builders. Forgetting either produces a
system that looks instrumented and emits nothing. `OpenTelemetry.Extensions.Hosting` exists
precisely so a library can register its own sources; use it.

**Files:**
- Modify: `src/Alberto.Dcb.Telemetry/Alberto.Dcb.Telemetry.csproj`
- Modify: `src/Alberto.Dcb.Telemetry/TelemetryBuilderExtensions.cs`
- Modify: `src/Alberto.Dcb.Telemetry/ServiceCollectionExtensions.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/TelemetryRegistrationTests.cs`

**Interfaces:**
- Consumes: `TelemetryOptions` (Task 1); `AlbertoModuleContext`, `AlbertoModuleDefinition` (Task 2); `DcbModuleBuilder.Configure` / `.Register` (Task 5).
- Produces: `WithTelemetry(this DcbModuleBuilder builder, Func<TelemetryOptions, TelemetryOptions>? configure = null)`. `AddAlbertoInstrumentation(this TracerProviderBuilder)` and `AddAlbertoInstrumentation(this MeterProviderBuilder)` stay, marked `[Obsolete]`, for applications that wire OpenTelemetry without the hosting integration.

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/TelemetryRegistrationTests.cs`:

```csharp
using System.Diagnostics;
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Alberto.Dcb.Tests.Configuration;

public class TelemetryRegistrationTests
{
    private static AlbertoModuleDefinition Resolve(IServiceCollection services) =>
        services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            .Get("orders");

    [Fact]
    public void WithTelemetry_marks_the_module_as_instrumented()
    {
        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());

        var definition = Resolve(services);

        definition.TelemetryEnabled.Should().BeTrue();
        definition.Telemetry.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Telemetry_can_be_switched_off_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alberto:Modules:orders:Telemetry:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());

        Resolve(services).Telemetry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Alberto_activities_are_collected_without_calling_AddAlbertoInstrumentation()
    {
        var exported = new List<Activity>();

        var services = new ServiceCollection();
        services.AddAlberto("orders", module => module.WithInMemory().WithTelemetry());
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(exported));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEnumerable<IHostedService>>();

        using var activity = AlbertoMetrics.ActivitySource.StartActivity("test-span");
        activity?.Stop();

        provider.GetRequiredService<TracerProvider>().ForceFlush();

        exported.Should().ContainSingle(a => a.OperationName == "test-span");
    }
}
```

> `AddInMemoryExporter` comes from `OpenTelemetry.Exporter.InMemory`. Add a `PackageVersion`
> entry for it in `Directory.Packages.props` at the same version as the other OpenTelemetry
> packages (1.15.3), then add `<PackageReference Include="OpenTelemetry.Exporter.InMemory" />`
> to `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`. If `AlbertoMetrics` does not expose a
> public `ActivitySource`, use whichever member the telemetry package already uses to start
> activities rather than adding a new one.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~TelemetryRegistrationTests"
```

Expected: the third test fails — the exporter collects nothing because the source is not registered.

- [ ] **Step 3: Add the hosting package**

In `src/Alberto.Dcb.Telemetry/Alberto.Dcb.Telemetry.csproj`, add to the `PackageReference` group:

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" />
```

- [ ] **Step 4: Self-register the sources and honour the options**

In `src/Alberto.Dcb.Telemetry/TelemetryBuilderExtensions.cs`, replace `WithTelemetry`:

```csharp
    /// <summary>
    /// Instruments this module: append interceptors, consume middleware, and — when the
    /// application uses the OpenTelemetry hosting integration — Alberto's activity source and
    /// meter, so no separate <c>AddAlbertoInstrumentation()</c> call is needed.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Transforms the telemetry options with a <c>with</c> expression.</param>
    public static DcbModuleBuilder WithTelemetry(
        this DcbModuleBuilder builder,
        Func<TelemetryOptions, TelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Configure(d => d with
        {
            TelemetryEnabled = true,
            Telemetry = configure is null
                ? d.Telemetry
                : configure(d.Telemetry)
                  ?? throw new InvalidOperationException("WithTelemetry configurator returned null."),
        });

        return builder.Register(context =>
        {
            var services = context.Services;
            var moduleKey = context.ModuleKey;

            // Registering the source and meter here means an application that already calls
            // AddOpenTelemetry() picks Alberto up automatically. Both calls are inert when the
            // hosting integration is absent.
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(AlbertoMetrics.Name));
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(AlbertoMetrics.Name));

            // Keep the existing keyed registrations from this method's previous body:
            //   ITraceContextProvider  -> ActivityTraceContextProvider
            //   IAppendInterceptor     -> TelemetryAppendInterceptor
            //   ConsumeMiddleware      -> TelemetryConsumeMiddleware.Create(provider)
            //   BatchConsumeMiddleware -> TelemetryBatchConsumeMiddleware.Create(provider)
            // all keyed by moduleKey, but guard each on the resolved options so
            // Telemetry:Enabled = false in configuration genuinely turns instrumentation off:
            //   var enabled = sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>()
            //       .Get(moduleKey).Telemetry.Enabled;
        });
    }
```

Add `using Alberto.Dcb.Configuration;`, `using Microsoft.Extensions.Options;` and
`using OpenTelemetry;` to the file.

- [ ] **Step 5: Deprecate the manual instrumentation calls**

In `src/Alberto.Dcb.Telemetry/ServiceCollectionExtensions.cs`, add to both
`AddAlbertoInstrumentation` overloads, above the method:

```csharp
    [Obsolete("Alberto registers its own activity source and meter from .WithTelemetry() when the " +
              "OpenTelemetry hosting integration is present. Call this only when configuring a " +
              "TracerProvider or MeterProvider outside the host.")]
```

Keep the bodies unchanged.

- [ ] **Step 6: Run the tests and the suite**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~TelemetryRegistrationTests" && dotnet build && dotnet test
```

Expected: PASS, 3 new tests; `Build succeeded`; all tests pass. Remove any now-redundant
`AddAlbertoInstrumentation()` calls in `apps/` that the obsolete warning surfaces.

- [ ] **Step 7: Commit**

```bash
git add -A src tests apps Directory.Packages.props && git commit -m "feat(telemetry): register Alberto's OpenTelemetry sources from WithTelemetry"
```

---

### Task 11: Orphaned checkpoint detection and `ops checkpoint rename`

Deriving processor ids from type names (Task 4) removes one hazard and introduces another:
renaming a handler class silently renames its checkpoint, and the processor restarts from zero.
This task makes that failure loud at startup and gives operators the one command that fixes it.

**Files:**
- Create: `src/Alberto.Dcb/Subscriptions/ICheckpointInventory.cs`
- Create: `src/Alberto.Dcb/OrphanCheckpointHostedService.cs`
- Modify: `src/Alberto.Dcb/ServiceCollectionExtensions.cs`
- Modify: `src/Alberto.Dcb.InMemory/InMemoryCheckpointStore.cs`
- Modify: `src/Alberto.Dcb.Postgres/PostgresCheckpointStore.cs`
- Modify: `tools/Alberto.Cli/Commands/Ops/CheckpointOpsCommand.cs`
- Test: `tests/Alberto.Dcb.Tests/Configuration/OrphanCheckpointTests.cs`

**Interfaces:**
- Consumes: `CheckpointOptions`, `OrphanCheckpointPolicy` (Task 1); `AlbertoModuleDefinition` (Task 2); `ProcessorDeclaration` (Task 2).
- Produces:
  - `ICheckpointInventory` with `Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)`
  - `OrphanCheckpointHostedService : IHostedService`
  - `alberto ops checkpoint rename --module <key> --from <old> --to <new>`

- [ ] **Step 1: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Configuration/OrphanCheckpointTests.cs`:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alberto.Dcb.Tests.Configuration;

public class OrphanCheckpointTests
{
    private sealed class FakeInventory(params string[] processorIds) : ICheckpointInventory
    {
        public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(processorIds);
    }

    private static AlbertoModuleDefinition Definition(
        OrphanCheckpointPolicy policy,
        params string[] declaredProcessorIds) => new()
    {
        ModuleKey = "orders",
        Checkpoints = new CheckpointOptions { OrphanPolicy = policy },
        Processors =
        [
            .. declaredProcessorIds.Select(id => new ProcessorDeclaration
            {
                ProcessorId = id,
                Kind = ProcessorKind.Reactor,
            }),
        ],
    };

    private static Task RunAsync(
        AlbertoModuleDefinition definition,
        ICheckpointInventory? inventory) =>
        new OrphanCheckpointHostedService(
            definition,
            inventory,
            NullLogger<OrphanCheckpointHostedService>.Instance)
            .StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Strict_fails_startup_when_a_checkpoint_has_no_processor()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary", "OldReactorName"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("OldReactorName");
        exception.Which.Message.Should().Contain("ops checkpoint rename");
    }

    [Fact]
    public async Task Strict_is_silent_when_every_checkpoint_is_claimed()
    {
        await RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary"));
    }

    [Fact]
    public async Task Warn_does_not_fail_startup()
    {
        await RunAsync(
            Definition(OrphanCheckpointPolicy.Warn, "OrderSummary"),
            new FakeInventory("OldReactorName"));
    }

    [Fact]
    public async Task Off_does_not_read_the_inventory()
    {
        await RunAsync(Definition(OrphanCheckpointPolicy.Off, "OrderSummary"), inventory: null);
    }

    [Fact]
    public async Task A_store_that_cannot_enumerate_is_skipped_rather_than_failing()
    {
        await RunAsync(Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"), inventory: null);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~OrphanCheckpointTests"
```

Expected: compile error — `The name 'ICheckpointInventory' does not exist`.

- [ ] **Step 3: Add the inventory capability**

Create `src/Alberto.Dcb/Subscriptions/ICheckpointInventory.cs`:

```csharp
namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// An optional capability on an <see cref="ICheckpointStore"/>: enumerating the processor ids
/// that currently have a stored position.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ICheckpointStore"/> so a custom store keeps working without it.
/// Alberto uses it to detect checkpoints left behind by a renamed processor; a store that does
/// not implement it simply opts out of that check.
/// </remarks>
public interface ICheckpointInventory
{
    /// <summary>Returns every processor id with a stored checkpoint, in no particular order.</summary>
    Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default);
}
```

Implement it on both built-in stores:

- `src/Alberto.Dcb.InMemory/InMemoryCheckpointStore.cs` — add `, ICheckpointInventory` to the class declaration and:

```csharp
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_checkpoints.Keys.ToList());
```

  Use whatever the backing dictionary field is actually called in that file.

- `src/Alberto.Dcb.Postgres/PostgresCheckpointStore.cs` — add `, ICheckpointInventory` and a query that mirrors the existing `GetAsync` implementation's table, schema handling and tenant filtering:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT processor_id FROM {_qualifiedTableName}";

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));

        return ids;
    }
```

  Match the field names (`_dataSource`, the qualified table name) that `GetAsync` already uses in
  that file; do not introduce new ones.

- [ ] **Step 4: Create the hosted service**

Create `src/Alberto.Dcb/OrphanCheckpointHostedService.cs`:

```csharp
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb;

/// <summary>
/// Compares the checkpoints in the store against the processors this module declares and reports
/// the ones nothing claims.
/// </summary>
/// <remarks>
/// An orphaned checkpoint almost always means a handler was renamed: the new name has no stored
/// position, so it replays from the beginning, while the old name's position sits unused. That is
/// silent, expensive, and easy to miss, so it is a warning in Development and a startup failure
/// everywhere else.
/// </remarks>
internal sealed class OrphanCheckpointHostedService(
    AlbertoModuleDefinition definition,
    ICheckpointInventory? inventory,
    ILogger<OrphanCheckpointHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (definition.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Off)
            return;

        if (inventory is null)
        {
            logger.LogDebug(
                "Skipping the orphaned-checkpoint check for module {ModuleKey}: the checkpoint " +
                "store does not implement ICheckpointInventory.",
                definition.ModuleKey);
            return;
        }

        var declared = definition.Processors
            .Select(p => p.ProcessorId)
            .ToHashSet(StringComparer.Ordinal);

        var stored = await inventory.ListProcessorIdsAsync(cancellationToken);
        var orphans = stored.Where(id => !declared.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

        if (orphans.Count == 0)
            return;

        var message =
            $"Module '{definition.ModuleKey}' has {orphans.Count} checkpoint(s) that no declared " +
            $"processor claims: [{string.Join(", ", orphans)}]. This usually means a handler was " +
            "renamed, in which case the new processor will replay from the beginning. " +
            $"Carry the position over with: alberto ops checkpoint rename --module {definition.ModuleKey} " +
            $"--from {orphans[0]} --to <new-processor-id>. Pin the old id instead with " +
            "[ProcessorId(\"...\")], or set " +
            $"'{definition.ConfigurationPath}:Checkpoints:OrphanPolicy' to Warn or Off.";

        if (definition.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Strict)
            throw new InvalidOperationException(message);

        logger.LogWarning("{OrphanCheckpointWarning}", message);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 5: Register it, and default the policy from the environment**

In `src/Alberto.Dcb/ServiceCollectionExtensions.cs`, inside `AddAlberto`, add to the
`AddOptions<AlbertoModuleDefinition>(moduleKey).Configure<IServiceProvider>(...)` callback —
**before** the `CopyInto(bound, definition)` line — the environment-derived default, so an
explicit code or configuration value still wins:

```csharp
                var environment = provider.GetService<IHostEnvironment>();
                if (environment is not null && !environment.IsDevelopment()
                    && bound.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Warn
                    && declared.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Warn)
                {
                    bound = bound with
                    {
                        Checkpoints = bound.Checkpoints with { OrphanPolicy = OrphanCheckpointPolicy.Strict },
                    };
                }
```

Then, in Phase 3 of the same method, after `final.Backend?.Register(context)`, register the check:

```csharp
        services.AddSingleton<IHostedService>(sp => new OrphanCheckpointHostedService(
            sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(moduleKey),
            sp.GetKeyedService<ICheckpointStore>(moduleKey) as ICheckpointInventory,
            sp.GetRequiredService<ILogger<OrphanCheckpointHostedService>>()));
```

Add `using Alberto.Dcb.Subscriptions;`, `using Microsoft.Extensions.Hosting;` and
`using Microsoft.Extensions.Logging;`. This registration sits after the backend's, so the
checkpoint store exists, and before the control loop's, so an orphan stops the host before any
processor replays.

> `Microsoft.Extensions.Hosting.Abstractions` supplies `IHostEnvironment`, `IHostedService` and
> `HostEnvironmentEnvExtensions.IsDevelopment`. `Alberto.Dcb` already references it for
> `IHostedService`; confirm with `grep -n "Hosting" src/Alberto.Dcb/Alberto.Dcb.csproj` and add
> `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />` if it is missing.

- [ ] **Step 6: Add the CLI rename command**

In `tools/Alberto.Cli/Commands/Ops/CheckpointOpsCommand.cs`, add a `rename` subcommand alongside
the existing `get`, `set` and `reset`, following the same option and output conventions those
already use:

```csharp
    private static async Task<int> RenameAsync(
        string moduleKey,
        string from,
        string to,
        ICheckpointStore store,
        CancellationToken ct)
    {
        var position = await store.GetAsync(from, ct);

        if (position is null)
        {
            Console.Error.WriteLine($"No checkpoint named '{from}' exists in module '{moduleKey}'.");
            return 1;
        }

        var destination = await store.GetAsync(to, ct);
        if (destination is not null)
        {
            Console.Error.WriteLine(
                $"'{to}' already has a checkpoint at position {destination}. " +
                "Reset it first if you really mean to overwrite it: " +
                $"alberto ops checkpoint reset --module {moduleKey} --processor {to}");
            return 1;
        }

        await store.SaveAsync(to, position.Value, ct);
        await store.ResetAsync(from, ct);

        Console.WriteLine($"Renamed checkpoint '{from}' to '{to}' at position {position.Value}.");
        return 0;
    }
```

Wire it up with `--module`, `--from` and `--to` options, matching how `set` declares `--module`
and `--processor` in the same file.

- [ ] **Step 7: Run the tests and the suite**

```bash
dotnet test tests/Alberto.Dcb.Tests --filter "FullyQualifiedName~OrphanCheckpointTests" && dotnet build && dotnet test
```

Expected: PASS, 5 new tests; `Build succeeded`; all tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A src tests tools && git commit -m "feat(checkpoints): detect orphaned checkpoints and add ops checkpoint rename"
```

---

### Task 12: Remove `DcbModuleBuilder.Services` and migrate every call site

Every backend now registers through a descriptor or `builder.Register(...)`, so the escape hatch
can go. Deleting it is what makes the ordering guarantee real rather than conventional.

**Files:**
- Modify: `src/Alberto.Dcb/DcbModuleBuilder.cs`
- Modify: `apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs`
- Modify: every remaining `builder.Services` call site the build reports
- Modify: `tests/Alberto.Dcb.Tests/**` — the ~50 existing test files

**Interfaces:**
- Consumes: everything from Tasks 5–11.
- Produces: `DcbModuleBuilder` with no `Services` property. Third-party backends use
  `IAlbertoBackendDescriptor`; everything else uses `builder.Register(context => ...)`.

- [ ] **Step 1: Delete the property**

In `src/Alberto.Dcb/DcbModuleBuilder.cs`, delete the `[Obsolete]` `Services` property and its
assignment in the constructor. Change the constructor to:

```csharp
    internal DcbModuleBuilder(string moduleKey) =>
        Definition = new AlbertoModuleDefinition { ModuleKey = moduleKey };
```

In `src/Alberto.Dcb/ServiceCollectionExtensions.cs`, change the construction to
`var builder = new DcbModuleBuilder(moduleKey);`.

- [ ] **Step 2: Find every break**

```bash
dotnet build 2>&1 | grep -E "error CS" | sort -u
```

Each error is a `builder.Services.X(...)` call. Wrap it:

```csharp
builder.Register(context => context.Services.X(...));
```

replacing `builder.ModuleKey` with `context.ModuleKey` inside the callback. Remove every
`#pragma warning disable CS0618` added in Task 5 Step 8.

- [ ] **Step 3: Migrate the Orders sample**

Replace the `services.AddAlberto(...)` call in
`apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs` with:

```csharp
        services.AddAlberto(ModuleKey, module => module
            .WithTenancy()
            .WithPostgres(o => o with
            {
                ConnectionString = connectionString,
                AutoMigrate = false,
                Schema = "orders",
                MaxPoolSize = 30,
            })
            .WithEntityFramework<OrdersDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
            })
            .WithTelemetry()
            .AddProjection(OrdersOverviewProjection.Declaration, sp =>
            {
                var dataSource = sp.GetRequiredKeyedService<NpgsqlDataSource>(ModuleKey);
                return () => new PostgresStateStore<OrdersOverview>(
                    dataSource, nameof(OrdersOverviewProjection), "orders");
            })
            .AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
            .WithControlLoop(o => o with
            {
                PollingInterval = TimeSpan.FromMilliseconds(100),
                BatchSize = 500,
            }));
```

- [ ] **Step 4: Migrate the test suite**

```bash
dotnet build tests/Alberto.Dcb.Tests 2>&1 | grep -E "error CS" | sort -u
```

Apply the same mappings from Tasks 6, 7 and 8. Two patterns recur:

- Tests that built a provider and expected migrations to have run must now either start a host or
  call `PostgresMigrator.Migrate(...)` in the fixture.
- Tests asserting on a thrown `InvalidOperationException` from a builder now assert on
  `OptionsValidationException` from `host.StartAsync(...)`, or on
  `new AlbertoModuleValidator().Collect(definition)` returning the expected code. Prefer asserting
  the code — it is stable, and the message is not.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded` with 0 warnings; all tests pass.

- [ ] **Step 6: Smoke-test the full stack**

```bash
dotnet run --project apps/Alberto.AppHost
```

Expected: PostgreSQL, the Orders API and the Admin web app all start; the Orders API logs
"Applying Alberto migrations for module orders" once and then serves GraphQL on port 5180. Stop
it with Ctrl-C.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor(config)!: remove DcbModuleBuilder.Services and migrate all call sites"
```

---

### Task 13: Migration guide and configuration reference

**Files:**
- Create: `UPGRADING.md`
- Create: `docs/configuration.md`
- Modify: `README.md` (create it if absent — the repository currently has none)

**Interfaces:**
- Consumes: the final public API from Tasks 1–12.
- Produces: documentation only.

- [ ] **Step 1: Write `UPGRADING.md`**

Create `UPGRADING.md` at the repository root with a `## 0.x → 1.0` section containing this table,
followed by a before/after of the Orders module taken verbatim from Task 12 Step 3:

| Change | What breaks | What to do |
|---|---|---|
| `DcbModuleBuilder.Services` removed | Third-party `.WithX()` extensions | Implement `IAlbertoBackendDescriptor` for a backend; use `builder.Register(context => ...)` for anything else |
| `Action<TOptions>` → `Func<TOptions, TOptions>` on `WithPostgres` | Every call site | `o => { o.X = y; }` becomes `o => o with { X = y }` |
| `PostgresOptions` is a record | Object initializers still work; assignment after construction does not | Use `with` |
| `ControlLoopBuilder` deleted | `.WithPollingInterval(...)` and siblings | `WithControlLoop(o => o with { PollingInterval = ... })` — see the mapping table in this file |
| `.WithMiddleware(...)` / `.WithBatchMiddleware(...)` moved | Control-loop-scoped middleware | Module-level `AddConsumeMiddleware(sp => ...)` / `AddBatchConsumeMiddleware(sp => ...)` |
| `ErrorPolicy` split | Custom classifiers | Retry knobs move to `ControlLoop.Retry`; the classifier moves to `UseErrorClassifier<T>()` |
| `ProcessorExecutionConfigurator` deleted | `configure: c => c.BatchIfSupported()` | `configure: o => o with { BatchingMode = ProcessorBatchingMode.IfSupported }` |
| `ReactTo<TEvent, THandler>` derives its processor id | Ids change from whatever string you passed to the handler's type name | Keep the old id with `[ProcessorId("...")]`, or carry the position over with `alberto ops checkpoint rename` |
| `ICheckpointStore` gains an optional `ICheckpointInventory` sibling | Nothing — it is a separate interface | Implement it on a custom store to opt into orphan detection |
| Migrations run at startup, not in `AddAlberto` | Code that built a provider and expected a migrated schema | Start the host, or call `PostgresMigrator.Migrate(...)` directly |
| `TenancyOrderingValidator` deleted | Nothing | `.WithTenancy()` may now appear anywhere in the chain |
| `AddAlbertoInstrumentation()` obsolete | A warning | Delete the call; `.WithTelemetry()` registers the source and meter |

Lead the file with the sentence that matters most: **processor ids are checkpoint keys, so the
`ReactTo` change is the only one that can silently reprocess your event log.** Point at
`alberto ops checkpoint rename` and at `Checkpoints:OrphanPolicy`.

- [ ] **Step 2: Write `docs/configuration.md`**

Document, in this order: the three phases; the `Alberto:Modules:{key}` layout with a complete
`appsettings.json` example covering `Postgres`, `ControlLoop` (including `Retry`, `DeadLetterRetry`
and `Leases`), `Telemetry` and `Checkpoints`; a table of every option, its default, and its
configuration key; the precedence rule (configuration beats code); the validation catalog
(`ALB0001`–`ALB0007`, `ALB1001`–`ALB1004`) with one line each; and how to write a custom backend
with `IAlbertoBackendDescriptor`.

Derive the option table from the records in Tasks 1 and 8 — do not restate defaults from memory.

- [ ] **Step 3: Write the README configuration section**

`README.md` does not exist. Create it with: what Alberto is (a DCB event store for .NET), the
install commands, a minimal working example, and a "Configuration" section that shows the Orders
module from Task 12 Step 3 and links to `docs/configuration.md` and `UPGRADING.md`. Keep it
short — the detail belongs in `docs/`.

- [ ] **Step 4: Verify every code sample compiles**

Paste each C# sample from the three documents into a scratch console project referencing
`src/Alberto.Dcb`, `src/Alberto.Dcb.Postgres` and `src/Alberto.Dcb.EntityFramework`, and build it.
Documentation that does not compile is worse than none.

- [ ] **Step 5: Commit**

```bash
git add README.md UPGRADING.md docs/configuration.md && git commit -m "docs: add the 1.0 upgrade guide and configuration reference"
```

---

## Self-Review

**Spec coverage.** Each spec section maps to a task: three-phase architecture → 2, 5; options
record table → 1, 8; overlay mechanism → 1, 2; processor identity → 4, 7, 11; validation catalog
→ 3, 8; telemetry wiring → 10; breaking changes → 6, 7, 8, 12, 13. The spec's out-of-scope items
(the `AddProjection` state-store factory, EF projection authoring ergonomics) stay out: Task 7
declares projections but leaves `AddProjection`'s signature alone, as the spec requires.

**Known gaps, stated rather than hidden.**

1. **Three "move this body" instructions.** Task 9 Step 4 (in-memory registration), Task 10
   Step 4 (telemetry keyed registrations) and Task 11 Step 3 (the Postgres inventory query)
   describe relocating existing code instead of reprinting it. Each names the exact file, the
   exact identifiers to change, and the registration keys to preserve. They are the honest
   boundary of what I could specify without the file contents in hand; an implementer should read
   the current body before moving it.
2. **Task 12 is large.** Migrating ~50 test files is mechanical but long. If it is too big for one
   review, split it: production call sites first, then tests.
3. **`ProjectionDeclaration<TState>`'s processor-id property name** (Task 7 Step 6) and the
   Postgres checkpoint store's field names (Task 11 Step 3) are referenced by role rather than by
   name — verify them in the files before writing the code.

**Type consistency.** `ControlLoopOptions.Retry` is `RetryOptions` everywhere (Tasks 1, 3, 6);
`AlbertoValidationFailure(Code, Problem, Remedy)` keeps that parameter order in Tasks 3, 8 and
`AlbertoModuleValidator`; `AlbertoModuleContext` exposes `Services`/`Definition`/`ModuleKey`/
`TenancyEnabled` and Tasks 6, 8, 9, 10 use only those; `ProcessorId.For<T>()` is spelled
identically in Tasks 4 and 7.

**One correction applied during review.** Task 5's `CopyInto` requires settable properties, which
contradicted Task 2's `init`-only, `required` declaration. Task 5 Step 6 now changes them to
`internal set` and drops `required`, and says so explicitly rather than leaving the two tasks in
conflict.
