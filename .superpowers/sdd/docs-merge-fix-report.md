# Docs merge-fix report

**Branch:** alberto-release-dx-dee517  
**Date:** 2026-07-25  
**Commit:** see bottom of this file

---

## Scratch-project compile result

All 9 C# samples were pasted into a fresh `net10.0` console project referencing
`Alberto.Dcb`, `Alberto.Dcb.Postgres`, and `Alberto.Dcb.InMemory` from source.

```
Build succeeded.
    9 Warning(s)   ← "unused local function" only; expected for a compile-check project
    0 Error(s)
```

---

## `dotnet build` result (full solution)

```
Build succeeded.
    127 Warning(s)   ← all pre-existing, all in apps/ not src/
    0 Error(s)
```

0 errors and 0 new warnings in `src/`.  
The 127 warnings are pre-existing (MessagePack NuGet vulnerabilities treated as
warnings in the AppHost, and an obsolete Aspire `WithCommand` overload in
`K6Resource.cs`). None is in `src/` and none is new.

---

## Files changed and sites fixed

### `docs/getting-started.md`

| Site | Before | After |
|---|---|---|
| §7 "Wiring" snippet (line ~150) | `.WithControlLoop(loop => loop.WithPollingInterval(TimeSpan.FromMilliseconds(50)))` | `.WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(50) })` |
| "Going to production" snippet (line ~169) | `WithPostgres(options => { options.ConnectionString = ...; options.Schema = ...; })` | `WithPostgres(o => o with { ConnectionString = ..., Schema = ... })` |
| Full program listing (line ~258) | same stale `WithControlLoop(loop => loop.WithPollingInterval(...))` | same fix as §7 |

---

### `docs/operations.md`

| Site | Before | After |
|---|---|---|
| §"Tuning the policy" code sample | `.WithControlLoop(loop => loop.WithErrorPolicy(p => new ErrorPolicy { ..., ErrorClassifier = p.ErrorClassifier, }))` | `.WithControlLoop(o => o with { Retry = o.Retry with { MaxRetries = 5, … } })` |
| Prose after that sample | "ErrorPolicy is a class with init properties, not a record, so there is no with expression" | "RetryOptions is an immutable record; use a with expression" + link to configuration.md |
| `MaxRetries` negative sentence | "A negative MaxRetries throws at construction" | "A negative MaxRetries is rejected at startup with validation code ALB0007" |
| §"Which failures are transient" classifier paragraph | "Supply your own by implementing IErrorClassifier and setting ErrorPolicy.ErrorClassifier" | "Supply your own classifier by implementing IErrorClassifier and calling UseErrorClassifier\<T\>() on the module builder" |
| Dead-letter retry loop config sample | `.WithControlLoop(loop => loop.WithRetryLoopPollingInterval(...).WithRetryLoopBatchSize(10).WithRetryLoopClaimLease(...))` | `.WithControlLoop(o => o with { DeadLetterRetry = o.DeadLetterRetry with { PollingInterval = …, BatchSize = …, ClaimLease = … } })` |
| "Set WithRetryLoopClaimLease longer…" | `Set WithRetryLoopClaimLease longer than your slowest handler.` | `Set ClaimLease longer than your slowest handler.` |
| §"Running more than one replica" sample | `.WithControlLoop(loop => loop.WithProcessorLeases())` | `.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })` (with ReplicaId comment) |
| Prose after leases sample | `WithProcessorLeases is required, not optional…` | `Enabling leases is required, not optional…` |
| §Telemetry code sample | `AddAlbertoInstrumentation()` on tracer/meter provider builders | Raw `AddSource("Alberto.Dcb")` / `AddMeter("Alberto.Dcb")` |
| §Telemetry prose | "Both halves are needed: WithTelemetry + AddAlbertoInstrumentation" | "WithTelemetry registers everything; AddAlbertoInstrumentation is [Obsolete]" + link to configuration.md |
| §Migrations WithPostgres sample | mutation-style `options.X = y` | `o => o with { X = y, … }` |

**Conflicting-accounts bug found:** `operations.md` taught `AddAlbertoInstrumentation()` as
required; `configuration.md` says it is `[Obsolete]`. Fixed `operations.md` to match
`configuration.md`.

---

### `docs/architecture/async-processing.md`

| Site | Before | After |
|---|---|---|
| Line ~51 | "ErrorPolicy.MaxRetries rejects negative values, which guarantees…" | "Negative MaxRetries is rejected at startup by validator ALB0007, which guarantees…" |
| §Configuration defaults table | Table headed "Defaults as of ControlLoopBuilder" with "Builder method" column naming `WithPollingInterval` / `WithErrorPolicy` | Table with "Code" and "Configuration key" columns using `WithControlLoop(o => o with { … })` syntax; link to configuration.md as canonical reference |
| Module config example (~line 141) | Mutation-style `WithPostgres(options => { options.X = y; })` and `.WithControlLoop(loop => loop.WithPollingInterval(...).WithBatchSize(...))` | `WithPostgres(o => o with { … })` and `.WithControlLoop(o => o with { PollingInterval = …, BatchSize = … })` |
| §"Module Configuration Example" prose after example | "ErrorPolicy is a class, not a record, so WithErrorPolicy takes a function that returns a new instance" + stale sample | "RetryOptions is an immutable record; use a with expression" + corrected sample; custom classifier via `UseErrorClassifier<T>()` noted |
| §"Enabling it" rebuild sample | `.WithControlLoop(loop => loop.WithRebuilds())` | `.WithRebuilds()` (WithRebuilds is a module-level extension, not inside the control loop lambda) |
| §"Limits" rebuild bullet | `Enable WithProcessorLeases if more than one replica…` | `Enable leases (.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } }))…` |
| §"Key Files" table | Row: `ErrorPolicy` → `src/Alberto.Dcb/Subscriptions/ErrorPolicy.cs` (file deleted) | Two rows: `RetryOptions` → `src/Alberto.Dcb/Configuration/RetryOptions.cs` and `IErrorClassifier` → `src/Alberto.Dcb/Subscriptions/IErrorClassifier.cs` |

---

### `docs/multi-tenancy.md`

| Site | Before | After |
|---|---|---|
| §"Multi-tenancy and rebuilds" | `need WithProcessorLeases, or two replicas will replay…` | `need leases enabled (.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })), or two replicas will replay…` |

---

### `docs/projections.md`

| Site | Before | After |
|---|---|---|
| §"Limits" bullet | `you need WithProcessorLeases, or two replicas replay…` | `you need leases enabled (.WithControlLoop(o => o with { Leases = o.Leases with { Enabled = true } })), or two replicas replay…` |

---

## Conflicting-defaults bug

No conflicting default values were found between `docs/configuration.md` and the
other documents. The one conflicting *account* (telemetry wiring in `operations.md`
vs `configuration.md`) is documented above and fixed.

## Samples not expressible in the new API

None. Every sample in the touched files has a direct equivalent in the 1.0 API.

## Sites deliberately left alone

- `UPGRADING.md` — migration guide; quoting the old API is its job.
- `docs/superpowers/plans/` and `docs/superpowers/specs/` — historical design records.
- `README.md` — already uses the 1.0 API (`WithControlLoop(o => o with { … })`); no changes needed.
- `docs/configuration.md` — the authoritative reference; left as the canonical source;
  other docs now link to it.
