# Alberto 1.0 — Configuration and Startup Validation

**Status:** approved design, not yet planned
**Date:** 2026-07-24
**Scope:** sub-project 1 of 5 in the release DX effort

## Context

Alberto is heading for a 1.0 release. A review of the developer-facing surface found
problems across five areas: configuration, authoring ergonomics for projections and
reactors, observability, testing support, and documentation. These are independent
enough that one spec would be unreviewable, so they are sequenced as separate
sub-projects. This spec covers the first.

Configuration comes first because the others hang off its shape: the testing kit needs a
way to build a module without a database, the observability work needs a place to put
telemetry options, and the docs cannot be written against an API that is about to change.

### The remaining sub-projects

2. Authoring ergonomics for projections and reactors
3. Observability — span and metric inventory, semantic conventions, health checks
4. Testing kit — a shipped `Alberto.Dcb.Testing` package
5. Documentation, samples, and packaging

These are listed for sequencing only. Nothing in this spec depends on their content.

## Problems being solved

**Three configuration idioms in one chain.** A single registration mixes mutable
property-set, fluent builder, and record-`with` styles:

```csharp
.WithPostgres(o => { o.ConnectionString = cs; o.Schema = "orders"; })
.WithControlLoop(l => l.WithPollingInterval(...).WithBatchSize(500))
.WithErrorPolicy(p => p with { MaxRetries = 5 })
```

**No configuration binding.** Every knob is hardcoded C#. Polling interval, batch size,
and pool size cannot be retuned per environment without a rebuild. There is no
`IConfiguration` binding, no `IOptions<T>`, and no `IValidateOptions<T>`.

**The fluent chain is order-dependent.** `.WithTenancy()` must precede `.WithPostgres()`,
because `WithPostgres` reads `builder.HasTenancy` at call time to choose single- versus
multi-tenant wiring. The hazard is real enough that `TenancyOrderingValidator`
(`src/Alberto.Dcb.Postgres/PostgresBuilderExtensions.cs`) exists solely to detect the
mistake after the fact. A declarative API where statement order silently changes
behavior is the most serious correctness issue in the current surface.

**`AddAlberto` performs database I/O during DI composition.** `WithPostgres` runs DbUp
migrations inline, and `ValidateTenancyMode` opens a connection even when `AutoMigrate`
is false. Building a `ServiceCollection` therefore requires a reachable database.

**Validation is late and deep.** Missing backend, duplicate processor IDs, missing
dead-letter store, and sync-reactor-with-batching are all checked inside
`IHostedService` factory lambdas in `src/Alberto.Dcb/ControlLoopBuilder.cs`. They fire at
first service resolve, one at a time, rather than at startup as a set.

**`processorId` is a free-form string that doubles as a checkpoint key.** A typo does not
error — it forks a checkpoint and the processor replays from position zero against a live
read model. Projections avoid this by convention (`nameof(...)`); reactors do not.

**Telemetry requires two disconnected calls.** `.WithTelemetry()` on the module and
`AddAlbertoInstrumentation()` on the `TracerProvider`/`MeterProvider`. Doing one without
the other costs full instrumentation overhead and produces no output, with no warning.

## Decisions taken

| Decision | Choice |
|---|---|
| Breaking changes | Free rein — this is the 1.0 reset. No obsolete shims. |
| Config source | Both: code sets defaults, `IConfiguration` overrides. |
| Idiom | Immutable records with `with` transforms. |
| Processor identity | Derived from handler type by default, override allowed. |
| Mistake prevention | Thorough startup validation. No Roslyn analyzers in this sub-project. |
| Config overlay | Nullable mirror records with an explicit `Apply`. |

## Architecture: deferred materialization

What is today one interleaved pass becomes three ordered phases.

### Phase 1 — Declare, during `AddAlberto`

Builder calls are pure. Each appends to or transforms an immutable
`AlbertoModuleDefinition` record: selected backend and its options transform, declared
processors, tenancy flag, telemetry flag. No services are registered, no I/O occurs, and
no call reads sibling builder state.

`.WithTenancy()` after `.WithPostgres()` therefore produces a definition identical to the
reverse order. `TenancyOrderingValidator` is deleted rather than retained.

`DcbModuleBuilder` loses its public `Services` property. Today every `With*` extension
reaches into `builder.Services` and registers directly, which is precisely what forces
order-dependence and eager I/O. The builder instead exposes a typed slot per concern —
backend, EF, telemetry, outbox, processors — and each extension fills its slot with a
*description* (for Postgres: a `PostgresOptions` transform plus a factory delegate) that
Phase 3 executes.

Third-party backends that were written against `builder.Services` break. The replacement
is a documented extension point, `IAlbertoBackendDescriptor`, not an internal mechanism
that happens to be public.

### Phase 2 — Bind and validate, at host start via `ValidateOnStart()`

Option records are materialized in precedence order: record defaults, then the code
`with` transforms, then `IConfiguration` overrides. The completed definition passes
through `IValidateOptions<AlbertoModuleDefinition>`, which reports every problem at once.

Binding uses `BindConfiguration("Alberto:Modules:{key}:...")`, which resolves
`IConfiguration` from DI lazily. `AddAlberto` keeps its current signature and the
getting-started path requires no configuration file.

### Phase 3 — Materialize, at host start after validation passes

The validated definition drives service construction, then hosted services start.
Migrations move here as an explicit ordered step running against validated options, which
means `AutoMigrate` finally respects a configuration override and building a
`ServiceCollection` no longer needs a database.

`WithEntityFramework<TDbContext>` keeps EF's own `Action<DbContextOptionsBuilder>`
verbatim. Wrapping another library's builder is worse for developers than a visible seam.

## Options records and binding

**Governing invariant: an options record contains only config-bindable values.** Anything
that cannot come from JSON — an `IErrorClassifier`, a `ConsumeMiddleware` delegate, a
`DbContextOptionsBuilder` action — lives on the definition, not in an options record.
"Which knobs can operations retune?" is answerable by reading the type, and the
silently-ignored-config-key problem is eliminated by construction rather than by
documentation.

This forces one split: today's `ErrorPolicy` mixes five bindable primitives with an
`IErrorClassifier` instance. It becomes bindable `RetryOptions` plus a separately
declared classifier.

| Record | Path under `Alberto:Modules:{key}:` | Replaces |
|---|---|---|
| `PostgresOptions` | `Postgres` | class → record, same shape |
| `ControlLoopOptions` | `ControlLoop` | `ControlLoopBuilder` private fields |
| `RetryOptions` | `ControlLoop:Retry` | `ErrorPolicy`, bindable half |
| `DeadLetterRetryOptions` | `ControlLoop:DeadLetterRetry` | `_retryLoop*` fields |
| `ProcessorLeaseOptions` | `ControlLoop:Leases` | `WithProcessorLeases(replicaId)` |
| `ProcessorExecutionOptions` | `Processors:{processorId}` | `ProcessorExecutionConfigurator` |
| `TelemetryOptions` | `Telemetry` | new |

Two fixes land in passing. `_headWindowSize` is currently a private field with no setter
at all and becomes a real knob. `ProcessorExecutionConfigurator.Build()` throws on
`WithConcurrency` without batching; that cross-field rule moves into `IValidateOptions`
so it is reported alongside every other problem rather than thrown mid-chain.

Per-processor overrides key off the derived processor ID, which is what makes
`Processors:SendConfirmationEmail:MaxConcurrency` work with no magic string in code.

### Resulting API

```csharp
services.AddAlberto("orders", alberto => alberto
    .WithTenancy()
    .WithPostgres(o => o with { ConnectionString = cs, Schema = "orders", AutoMigrate = false })
    .WithEntityFramework<OrdersDbContext>(o => o.UseNpgsql(cs))
    .WithTelemetry()
    .AddProjection(OrdersOverviewProjection.Declaration, sp => /* unchanged */)
    .AddEfProjection<OrderSummaryEntity, OrdersDbContext>(OrderSummaryEfProjection.Declaration)
    .WithControlLoop(o => o with { PollingInterval = TimeSpan.FromMilliseconds(100), BatchSize = 500 }));
```

```jsonc
// appsettings.Production.json — overrides the code above, no rebuild
"Alberto": { "Modules": { "orders": {
  "Postgres":    { "MaxPoolSize": 50 },
  "ControlLoop": { "BatchSize": 200, "PollingInterval": "00:00:00.250" },
  "Processors":  { "SendConfirmationEmail": { "MaxConcurrency": 4 } }
}}}
```

### Overlay mechanism

Immutable records and `Microsoft.Extensions.Configuration` do not compose cleanly. The
Options pattern is mutate-in-place — `IConfigureOptions<T>.Configure(T options)` returns
void — so it cannot express `o => o with { ... }`. And `ConfigurationBinder.Bind(instance)`,
the only call that performs a *partial* overlay setting just the keys present in
configuration, has unreliable support for `init`-only properties: the reflection binder
handles them, the source-generated binder that .NET 10 prefers and that trimming and AOT
require does not reliably.

Each options record therefore gets a sibling with all-nullable properties — for example
`ControlLoopOverrides`. Configuration binds to the mirror, which is a plain DTO with no
binder edge cases, then a hand-written `Apply` walks non-null values onto the
code-configured record. This is explicit, trim- and AOT-clean, and unit-testable.

The cost is one mirror type and one `Apply` per options record, kept in sync by hand. A
single reflection-based parity test asserting that every options record's property set
matches its mirror's catches drift at build time — roughly thirty lines once, not per
record.

## Processor identity

**Reactors derive their ID from the handler class.**
`ReactTo<OrderCreated, SendConfirmationEmail>(h => h.HandleAsync)` yields
`"SendConfirmationEmail"` with no string present.

**The lambda overloads continue to require an explicit name**, because an anonymous
delegate has no name to derive from. This is a useful forcing function rather than a gap:
the handler-class form is also the more testable form, so the ergonomic path and the good
path coincide.

**Renaming is handled by attribute, not by parameter.** `[ProcessorId("SendConfirmationEmail")]`
on the handler class pins the checkpoint key while the class is renamed freely. Placing
it on the class rather than in the registration call matters — the override sits next to
the thing being renamed, so whoever renames the class sees it.

**Projections keep their explicit declaration name.**
`DeclareProjection.For<OrdersOverview>(nameof(OrdersOverviewProjection))` names the
projection class, not the state type, so no correct type exists to derive from. For a
checkpoint key that must survive years of refactoring, an explicit stable string is the
better default. Validation enforces only that it is non-empty and unique.

### Orphaned checkpoint detection

Derived IDs move the failure mode from typo to rename, and a rename is silent today: the
new ID has no checkpoint, so the processor replays from zero against a live read model.

At startup Alberto compares declared IDs against checkpoint rows and reports checkpoints
whose ID is no longer declared:

```
Module 'orders': checkpoint 'SendEmail' exists at position 4,231,908 but no processor
declares that ID. Processor 'SendConfirmationEmail' has no checkpoint and will replay
from position 0.

If this is a rename:   [ProcessorId("SendEmail")] on SendConfirmationEmail
                       or: alberto ops checkpoint rename --from SendEmail --to SendConfirmationEmail
If this is intentional: alberto ops checkpoint dismiss --id SendEmail
```

This requires three things:

- `alberto ops checkpoint rename` added to the CLI, which already has
  `ops checkpoint get/reset/set`.
- A `Strict | Warn | Off` knob. A genuinely removed processor must not block startup
  forever, and blue/green deploys legitimately run old and new IDs side by side.
- The ability to skip the check entirely, since it costs a checkpoint-table read at
  startup that an in-memory test host should not pay.

Default: `Warn` in Development, `Strict` in Production, keyed off `IHostEnvironment`.

## Validation and error reporting

Validation splits by whether it needs I/O.

### Phase 2 — pure checks

Run via `IValidateOptions` and `ValidateOnStart()`. No database, so these execute in unit
tests and in a `WebApplicationFactory`.

*Topology:* no backend declared; two backends declared; duplicate processor IDs after
derivation; sync reactor with batching enabled; `MaxConcurrency > 1` with batching
disabled; projection declared with no resolvable state store; dead-lettering enabled with
no dead-letter store; duplicate or blank module keys; processor IDs containing characters
unsafe as a checkpoint key.

*Option values:* `BatchSize >= 1`; `PollingInterval > 0`; `MaxPoolSize >= MinPoolSize`;
`MaxRetryDelay >= RetryDelay`; `BackoffMultiplier >= 1`; `ClaimLease > 0`. Simple bounds
as `[Range]` and `[Required]` data annotations; cross-field rules in `IValidateOptions`.

*Telemetry:* `AddAlbertoInstrumentation()` called without `.WithTelemetry()`. The reverse
case is eliminated by design — see below.

*Unknown configuration keys.* Because the mirror records enumerate every legal key under
`Alberto:`, that set can be diffed against what configuration actually provides.
`ControlLoop:BatchSizes` is silently ignored by every stock .NET application; here it
fails with a did-you-mean suggestion. This is the highest-value item in the section:
configuration typos survive code review, pass CI, and surface only as "why is production
still using the default?".

### Phase 3 — checks needing the database

One `IHostedService` ordered ahead of the control loops: schema tenancy-mode match
(relocated from the eager connect in `WithPostgres`), migration state, and orphaned
checkpoints. These aggregate among themselves, so fixing one does not reveal the next on
the following run.

### Reporting

Every phase collects all failures before throwing; never first-failure-wins. Phase 2
retains the standard `OptionsValidationException` that `ValidateOnStart()` produces, so
no unfamiliar type escapes. Only message content is controlled.

```
Alberto configuration is invalid (3 problems).

module 'orders'
  ✗ Processors 'SendConfirmationEmail' and 'ConfirmationEmailSender' both resolve to
    processor ID 'SendConfirmationEmail'. They would share checkpoint state and each
    would silently skip events the other advanced past.
    → Add [ProcessorId("...")] to one of them.

  ✗ ControlLoop.BatchSize is 0, must be at least 1.
    → Alberto:Modules:orders:ControlLoop:BatchSize in appsettings.Production.json

  ✗ Unknown configuration key 'Alberto:Modules:orders:ControlLoop:BatchSizes'.
    → Did you mean 'BatchSize'?
```

Each entry states what is wrong, why it matters where that is not obvious, and where to
fix it — including the configuration path when the value came from configuration rather
than code, so nobody hunts through four appsettings files to find which one won.

## Telemetry wiring

In scope: making telemetry a single call. Out of scope and deferred to sub-project 3:
span and metric inventory, semantic conventions, health checks.

`.WithTelemetry()` self-registers through `ConfigureOpenTelemetryTracerProvider` and
`ConfigureOpenTelemetryMeterProvider` from `OpenTelemetry.Extensions.Hosting` — the
extension points that exist so libraries can add their own sources without the
application restating them.

```csharp
builder.Services.AddAlberto("orders", m => m
    .WithTelemetry(o => o with { RecordEventPayloadSize = false }));
```

If the application never calls `AddOpenTelemetry()`, those registrations are inert: no
failure, nothing exported, no dependency on the application's OTel setup order.

The cost is a package reference from `Alberto.Dcb.Telemetry` to
`OpenTelemetry.Extensions.Hosting`. It is already pinned at 1.15.3 in
`Directory.Packages.props`, and for a package whose entire purpose is telemetry the
dependency is defensible.

`AddAlbertoInstrumentation()` remains public, because anyone wiring a `TracerProvider`
manually outside DI still needs it. It becomes idempotent so calling both is harmless.

### Noted, not decided here

`.WithTelemetry()` is opt-in, so the default experience produces no traces. For a 1.0
event store this is arguably the wrong default: the cost with no exporter configured is
near zero, and "I set up OpenTelemetry and Alberto shows nothing" is a poor first
impression. Flipping it to on-by-default is a behavior change beyond the configuration
API and is recorded as a recommendation for sub-project 3.

## Testing

Phase 1 is pure and Phase 2 needs no I/O, so nearly all of this is unit-testable without
a database.

- **Order independence.** Permutations of a chain — `WithTenancy` before and after
  `WithPostgres`, and so on — must produce an identical `AlbertoModuleDefinition`. This
  test replaces `TenancyOrderingValidator`: it proves the class of bug is gone rather
  than detected.
- **Precedence.** Defaults → code `with` → configuration overlay, asserted per options
  record, including that a code-set value survives an unrelated configuration key and
  that a configuration key wins over a code-set one.
- **Mirror parity.** Reflection over each options record and its overrides mirror. Adding
  a knob without its override fails the build.
- **No I/O during composition.** `AddAlberto` with `WithPostgres` pointed at an
  unreachable host must succeed; failure must appear only at host start. This is the
  regression guard for the eager-migration behavior.
- **Validation catalog.** One test per rule, asserting message text rather than merely
  that it threw. Messages are the deliverable here, so they are asserted like any other
  output. The aggregated report gets a golden-file test.
- **Unknown-key detection**, including the did-you-mean suggestion.
- **Postgres integration via Testcontainers**, for Phase 3 only: tenancy-mode mismatch,
  migration state, orphaned checkpoints.

The existing suite in `tests/Alberto.Dcb.Tests` has roughly fifty files that construct
modules through the current API. Migrating them is a substantial part of the work and is
sized as such rather than discovered mid-implementation.

## Breaking changes

All documented in `UPGRADING.md` with before/after examples.

| Change | Impact |
|---|---|
| `DcbModuleBuilder.Services` removed from public API | Breaks third-party `.WithX()` extensions; replaced by `IAlbertoBackendDescriptor` |
| `Action<TOptions>` → `Func<TOptions,TOptions>` on all `With*` | Every call site; mechanical |
| `PostgresOptions` class → record | Mechanical |
| `ControlLoopBuilder` deleted, replaced by `ControlLoopOptions` | `WithMiddleware` and `WithBatchMiddleware` move to module-level `AddConsumeMiddleware(...)` |
| `ErrorPolicy` split into `RetryOptions` plus declared classifier | Custom classifiers re-register |
| `ProcessorExecutionConfigurator` deleted | `BatchIfSupported()` → `o with { BatchingMode = ... }` |
| `ReactTo(..., processorId)` → derived with attribute override | Explicit IDs keep working via `[ProcessorId]` |
| Migrations move from `AddAlberto` to a startup hosted service | Behavior change for `AutoMigrate = true` |
| `TenancyOrderingValidator` deleted | No replacement needed; the ordering hazard is gone |

## Risks

**The rewrite reaches into the subtle code.** `ControlLoopBuilder.Build()` is not merely
registration: it carries the fence-violation callback wiring, lease-group construction,
and the `hasUnpairedPerEventMiddlewares` batching-fallback logic. Regressions there are
silent and expensive. Mitigation: move that logic wholesale into Phase 3 without
reshaping it, and treat any change to its behavior as out of scope for this sub-project.

**Migration relocation changes startup ordering** for `AutoMigrate = true` users. The
reference application already runs migrations from a separate project for Aspire
sequencing, so the path is not exercised there — the behavior change is least likely to
be caught by the application most likely to be tested. It needs a dedicated integration
test.

**Derived processor IDs plus orphan detection can block a deploy** when someone renames a
handler and does not read the message. That is the intended behavior, and it is why the
`Strict | Warn | Off` knob and the environment-based default are load-bearing rather than
optional.

## Out of scope

- Roslyn analyzers. Considered and deferred; startup validation covers the realistic
  misconfigurations at a fraction of the cost.
- A structured startup diagnostics report. Considered and deferred to sub-project 3,
  where it belongs with the rest of observability.
- Any change to event processing semantics: batching, fencing, leases, dead-lettering
  behavior. This sub-project moves that code without reshaping it.
- The `AddProjection` state-store factory argument. Inferring a state store from the
  declared backend would be an authoring-ergonomics improvement and belongs to
  sub-project 2. `AddProjection` keeps its current two-argument shape here, changing only
  the idiom of any options it takes.
- The four remaining DX sub-projects.
