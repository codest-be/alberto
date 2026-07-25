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
          }
        },
        "Telemetry": {
          "Enabled": true,
          "RecordEventPayloadSize": true
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

---

## Telemetry options

`.WithTelemetry(o => o with { ... })` · configuration path: `Telemetry`

| Property | Type | Default | Configuration key |
|---|---|---|---|
| `Enabled` | `bool` | `true` | `Telemetry:Enabled` |
| `RecordEventPayloadSize` | `bool` | `true` | `Telemetry:RecordEventPayloadSize` |

`.WithTelemetry()` registers Alberto's activity source and meter with the OpenTelemetry
hosting integration (`services.AddOpenTelemetry().WithTracing(...).WithMetrics(...)`). If
the application never calls `AddOpenTelemetry()`, this installs the SDK with no exporters,
which is effectively inert. `AddAlbertoInstrumentation()` (on `TracerProviderBuilder` /
`MeterProviderBuilder`) is `[Obsolete]` — delete it; `.WithTelemetry()` does the same
work automatically.

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
honoured. This asymmetry matters: setting `OrphanCheckpointPolicy.Warn` in code is
indistinguishable from the default and still escalates. The configuration key is the
reliable opt-out.

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
| `ALB0004` | A control loop duration or count is ≤ 0 (`PollingInterval`, `HeadRefreshInterval`, `BatchSize`, or `HeadWindowSize`) | Set a positive value in code or configuration |
| `ALB0005` | `MaxConcurrency > 1` with `BatchingMode = Disabled` — concurrency only applies within a batch | Set `BatchingMode` to `IfSupported` or `Required`, or reduce `MaxConcurrency` to 1 |
| `ALB0006` | A processor id is empty or contains whitespace | Use a non-empty identifier without whitespace |
| `ALB0007` | `Retry.MaxRetries < 0` or `Retry.BackoffMultiplier < 1.0` | Use 0 to disable retries; use 1.0 for a constant backoff delay |

### Postgres codes (ALB1xxx)

Reported by `PostgresBackendDescriptor.Validate`, called from the same pass.

| Code | Condition | Remedy |
|---|---|---|
| `ALB1001` | `ConnectionString` is empty | Set it in code or via `Postgres:ConnectionString` |
| `ALB1002` | `MaxPoolSize ≤ 0` | Set a positive pool size |
| `ALB1003` | `MinPoolSize > MaxPoolSize` | Lower `MinPoolSize` or raise `MaxPoolSize` |
| `ALB1004` | `LeaseDuration ≤ 0` | Set a positive duration |

---

## Custom backends

Implement `IAlbertoBackendDescriptor` to plug in a third-party event store. The four
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

A minimal implementation:

```csharp
using Alberto.Dcb.Configuration;
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
