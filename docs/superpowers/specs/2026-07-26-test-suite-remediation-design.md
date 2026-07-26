# Test suite remediation — design

**Date:** 2026-07-26
**Scope:** `tests/`, `src/Alberto.Dcb.Testing*` (new), targeted changes in
`src/Alberto.Dcb`, `src/Alberto.Dcb.Postgres`, `apps/`
**Status:** approved, not yet implemented

## Problem

The suite is in better shape than its reputation: 911 tests, 909 passing, 2
skipped, in 18.5 seconds — including roughly 219 tests that go to PostgreSQL.
The container consolidation described in
[2026-07-25-test-container-consolidation-design.md](2026-07-25-test-container-consolidation-design.md)
did its job. Speed is not the problem, and no recommendation here is motivated
by wall-clock.

What the audit found instead is four distinct problems.

**There is almost no shared test vocabulary.** In 20,181 lines of tests there
is exactly one shared helper, `Testing/EventCollector.cs`. Everything else is
re-declared per file:

| Declared in tests | Times |
|---|---|
| `OrderCreated` | 7 |
| `OrderConfirmed` | 5–6 |
| `OrderCancelled` | 5 |
| `FakeBackend`, `DelegatingProcessor` | 3 each |
| `InMemoryStateStore`, `InMemoryOutboxStore`, `TestProcessor`, `OrderShipped`, `CounterIncremented`, `SummaryHandler`, `AlwaysPermanentClassifier`, `InlineStateStoreProjection`, `Counter` | 2 each |

The duplicates are not copies. The three `FakeBackend` declarations differ
behaviourally — one takes configurable tenancy and injectable validation
failures, one tracks whether `Register` ran and what `TenancyEnabled` was at the
time, one hardcodes `SupportsTenancy => false`. Any canonical replacement has to
be a configurable superset, not a promotion of whichever copy was read first.
`InMemoryStateStore` is re-implemented in tests while
`src/Alberto.Dcb.InMemory/InMemoryStateStore.cs` exists.

**Timing is asserted with wall-clock sleeps.** Fifteen `Task.Delay` calls stand
in for synchronisation, from 10 ms to 300 ms, across `PostgresDeadLetterStore`,
`ControlLoop`, `CachingCheckpointStore`, `DeadLetterRetryLoop`,
`EventStoreHeadBarrier`, `ProcessorLeaseManager`, `ProjectionRebuildEndToEnd`,
`OutboxRelay`, `TenantProcessorLock`, `ControlLoopMiddleware` and
`PostgresEventListener`. Three different polling helpers exist with divergent
semantics — `WaitForAsync` with a 10 s ceiling failing via `Assert.Fail`,
another copy of the same, and `WaitUntilAsync` with a hardcoded 30 s ceiling
throwing `TimeoutException`.

The root cause is that the `TimeProvider` seam stops at the backends. Only six
files under `src/` use it. Nine call `DateTime(Offset).UtcNow` directly, and
`CachingCheckpointStore` builds two raw `Timer`s in its constructor with no seam
at all.

**The operator surface is untested.** Every CLI command type in
`tools/Alberto.Cli` — `StatusCommand`, `CheckpointsCommand`,
`DeadLettersCommand`, `EventsCommand`, `ProcessorCommand`, `ProjectionsCommand`,
`ShardsCommand`, `SystemCommand`, `TenantsCommand`, `OpsCommand`,
`CheckpointOpsCommand`, `DeadLetterOpsCommand`, `RebuildCommand`,
`TenantOpsCommand` — plus `ConnectionResolver`, `ConfigFileFinder`,
`HumanOutput`, `JsonOutput`, `ShardRun` and `CommandLineCompat` have no direct
tests. This is the surface an operator reaches for when production is on fire,
and it is 3,557 lines of untested code that can move checkpoints backwards.

Also untested: the append interceptor pipeline, the whole of
`src/Alberto.Dcb.Telemetry`, `RetryAndDeadLetterCore`, the rebuild types
(`RebuildCoordinator`, `ShadowProcessor`, `ShadowControlLoopFactory`,
`RebuildableProjection`), the control loop groups, and both migration hosted
services.

**Two documented rebuild defects make two tests flaky.**
`Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection` and
`AbortedRebuild_LeavesTheLiveVersionUntouched` fail roughly one run in twenty
under five concurrent test processes, and never on an unloaded machine.

## Goals

In priority order, as agreed:

1. **Confidence before the NuGet release.** `publish-packages.yml` already ships
   betas from `src/**` and `tools/**` on every push to `main`. Whatever is wrong
   is being published.
2. **Protect the operator CLI surface.**
3. **The example apps must work, and must double as a demonstration of the
   testing infrastructure.** A consumer should be able to read
   `apps/Alberto.Orders`' tests and see how Alberto expects to be tested.
4. **Stop the flakiness.**

## Non-goals

- Making the suite faster. It is already fast enough.
- Pushing `TenantIsolationTests` down to unit level. Those tests assert that
  queries carry a `WHERE tenant_id` clause; proving that against a fake proves
  nothing. They stay at integration level.
- General refactoring of `src/` beyond what the above requires.
- Resolving the outbox orphan-reclaim gap. It gets a home in a specification
  and a documented skip, not a fix.

## Decomposition

| | Sub-project | Serves | Character | Size |
|---|---|---|---|---|
| SP0 | Coverage collection in CI | — | `coverlet.collector` + `--collect` | XS |
| SP1a | Build the `Alberto.Dcb.Testing` packages | 1, 3 | new files only | M |
| SP1b | Migrate the existing suite onto them | 1, 3 | sweeping edits, ~85 files | M |
| SP2 | `TimeProvider` seams, delete the wall-clock waits | 4 | small `src` + ~5 test files | S |
| SP3 | Operator CLI coverage | 2 | new tests, low risk | M |
| SP4 | Rebuild subsystem correctness | 1, 4 | **product change** | L |
| SP5 | Example apps as testing showcase | 3 | new test projects | M |
| SP6 | Consistency sweep | — | mechanical | S |
| SP7 | Per-tenant projection writes | 3 | **product change** | M |

### Waves

**Wave 1, parallel ×4** — SP0, SP1a, SP2, SP4. SP4 starts first as the longest
pole. SP2 and SP4 both live under `src/Alberto.Dcb/Subscriptions/` but touch
disjoint files: SP2 has `CachingCheckpointStore`, `DeadLetterRetryLoop` and the
two `*ConsumeMiddleware`; SP4 has `RebuildCoordinator`, `RebuildableProjection`
and `ProjectionVersions`.

**Wave 2, parallel ×3** — SP1b, SP3, SP7. SP1b and SP3 are gated on SP1a, which
gives them the vocabulary to write against; SP1b is the only high-conflict item,
and SP3 adds new files and rebases cleanly. SP7 is gated on SP4 instead, because
both touch the rebuild version plumbing — SP4 changes when a version's rows are
deleted, SP7 changes which store instance writes them.

**Wave 3, gated on SP7** — SP5. Its overview tests cannot pass until SP7 lands;
see below.

**Wave 4, solo** — SP6. It rewrites assertions across every file, including
everything the other sub-projects just wrote, so it goes last and alone.

Wave 1 branches should use git worktrees so four concurrent builds do not
contend over `obj/`.

## SP1a — the testing packages

Helpers fall into three categories, and only two of them ship.

**Customer-facing — ships as `Alberto.Dcb.Testing`.** What someone building on
Alberto needs to test their own application, and therefore what SP5 must
demonstrate:

- A module test harness: stand up an Alberto module over the in-memory backend,
  append events, drive the control loop to quiescence, assert projection state.
  This replaces the ad-hoc `ServiceCollection` → `BuildServiceProvider` → poll
  sequence repeated across the suite.
- One polling helper, replacing the three divergent copies. It must be
  assertion-library-neutral, so it throws rather than calling `Assert.Fail`.
- `InMemoryOutboxStore`. `IOutboxStore` has exactly one implementation, and it
  is the PostgreSQL one; a consumer testing outbox behaviour has nothing today,
  while the suite declares its own twice.
- Event construction helpers, so tests stop hand-rolling
  `EventTypeAttribute.GetEventTypeId`.

This package takes no dependency on a test framework.

**Backend-implementer-facing — ships as `Alberto.Dcb.Testing.Xunit`.** The
contract specifications: `StateStoreSpecification` (three implementations,
currently no shared spec), `DeadLetterStoreSpecification`
(`InMemoryDeadLetterStore` has zero direct tests today),
`OutboxStoreSpecification`, plus `EventStoreBackendSpecification` and
`CheckpointStoreSpecification` promoted out of `tests/`. A third party writing a
backend can then run Alberto's own conformance suite against it.

A specification is only useful if the derived class inherits runnable tests, so
this package depends on xunit.v3. Keeping it separate means a consumer who only
wanted `InMemoryOutboxStore` is not forced onto xunit — a constraint that cannot
be walked back once published.

**Internal — stays in `tests/`.** `FakeBackend : IAlbertoBackendDescriptor`,
validator scaffolding, the config-test `ProcessorDeclaration` builders.
Consumers do not implement backend descriptors. The canonical `FakeBackend` is
a configurable superset of the three current copies: settable
`SupportsTenancy`, injectable `AlbertoValidationFailure[]`, and `Registered` /
`TenancyAtRegistration` tracking.

The duplicated event vocabulary (`OrderCreated` and friends) ships nowhere. It
collapses into one internal `Testing/Events.cs`. The example apps have real
domain events and must not borrow test ones.

`EventCollector` is promoted into the customer-facing package, with its
internal `DateTimeOffset.UtcNow` replaced by an injected `TimeProvider`
defaulting to `TimeProvider.System`.

Because `publish-packages.yml` will begin shipping both packages as betas on the
next push to `main` touching `src/**`, their public surface is a versioning
commitment from the first beta onward.

## SP4 — rebuild correctness

The promotion race and the abort lag are one defect seen from two sides: **rows
are deleted inside the transaction that changes the version, before the other
parties know the version changed.**

- On promote, the other party is a reader holding a version number cached by
  `ProjectionVersions`, refreshed on a 5-second loop. It resolves a version,
  then queries it, and the rows vanish in between.
- On abort, the other party is the shadow loop, which learns of the abort on its
  next poll and lands writes after the delete has run.

### Design

Neither promote nor abort deletes. Both stamp the superseded or abandoned
version as retired, with a timestamp. A sweep deletes retired versions whose
retirement is older than a grace period. Grace must exceed both the
`ProjectionVersions` refresh interval and the shadow loop's poll interval, so
that a stale reader always finds rows and a late shadow write always lands
before collection.

Grace is configurable, defaulting to four times the `ProjectionVersions` refresh
interval with a floor of 30 seconds — so 30 seconds under the default 5-second
refresh. The multiplier buys headroom for a refresh that fails and leaves the
previous versions in place, which `RefreshLoopAsync` does deliberately. The
configured value is validated at startup to exceed both intervals it depends on,
because a grace period shorter than the refresh interval reintroduces exactly
the defect this design removes, and would do so silently.

This needs migration 019 adding retirement columns to the rebuild meta table, in
both the multi-tenant and single-tenant variants. The existing abort sweep
becomes the general collector.

**The deterministic seam:** the cutoff timestamp is computed by the caller from
a `TimeProvider` and passed into the sweep query as a parameter, rather than
`now()` evaluated server-side. That is what makes the grace period testable with
`FakeTimeProvider` instead of a sleep, and it is why SP4 and SP2 share a design
despite touching disjoint files.

### Consequences for the two flaky tests

`Rebuild_ReplacesCorruptedState_WithoutEverServingAPartialProjection` becomes
true rather than true-under-low-load.

`AbortedRebuild_LeavesTheLiveVersionUntouched` splits in two: one test asserts
the live version is untouched, which is what its name claims; a second asserts
the abandoned version is collected once the grace period elapses, driven by
advancing a fake clock. The current test asserts emptiness immediately after
abort, which the design will now explicitly not promise.

## SP7 — per-tenant projection writes

`OrderQueries.GetOrdersOverview` and `PaymentQueries.CreateStateStore` build
their `PostgresStateStore` with `tenantId:` set, while `OrdersModule` and
`PaymentsModule` construct theirs without one. `PostgresStateStore` switches on
`tenantId is not null`: multi-tenant mode filters on `WHERE tenant_id` and keys
on `(tenant_id, projection_type, document_id, rebuild_version)`, single-tenant
mode omits the column. Rows therefore land under one primary key and are read
back under another, so `getOrdersOverview`, `getPaymentsOverview` and
`getRecentPayments` return nothing however many events have been consumed.

### Correcting the record

The Known Gaps entry in `CLAUDE.md` states the writers cannot be switched to
match. That reasoning does not survive contact with the code, and this design
supersedes it. `IEventEnvelope.TenantId` exists, and
`ProjectionContext.FromEnvelope` already copies it through — the tenant is on
every event at the exact point `DeclaredAsyncProjection` writes. The entry
conflates two separate things: it argues the tenant "cannot be folded into the
key" because `On<TEvent>` hands the document-id selector only the parsed event,
but the tenant does not belong in the document id, it belongs at the store; and
it argues the consumer path runs with `HasTenant=false`, which only matters if
the tenant is resolved ambiently rather than read off the envelope.

Updating that entry is part of SP7.

### Rejected alternative

**Tenant on the `IStateStore` call signature** — `LoadManyAsync(keys, tenantId,
ct)`, `ApplyChangesAsync(upserts, deletes, tenantId, ct)`. It forces every
implementation to confront tenancy, but it breaks a public interface and then
dies on `EfStateStore`: EF entities carry their tenant as an ordinary column
that the *handler* sets, so a generic store cannot filter by it without knowing
the entity's tenant property. The parameter would have to be silently ignored
there — a lie on a public contract, worse than the problem it solves.

**Dropping `tenantId` from the three readers** was also considered and rejected.
It is a three-line change, but it permanently makes the JSONB aggregates
cross-tenant. Since SP5 exists to be a reference implementation, shipping a
multi-tenant event store whose showcase read models leak across tenants is the
wrong demonstration.

### Design

The store factory becomes per-tenant: `Func<IStateStore<TState>>` becomes
`Func<string?, IStateStore<TState>>`. `DeclaredAsyncProjection` caches stores
per tenant, and `ProcessBatchAsync` groups the batch by `evt.TenantId` before
loading and applying.

This is the shape the codebase already reaches for. `PostgresStateStore` takes
`tenantId` in its constructor and switches its entire DML shape on it; it was
built to be a per-tenant instance. The defect is only that the writer
constructs exactly one, forever. `IStateStore` itself does not change, so
SP1a's `StateStoreSpecification` is unaffected. EF keeps its single-store model,
where isolation already works via the entity column.

Three registration sites change: `DcbModuleBuilderExtensions.cs` and two in
`EfConsumerBuilderExtensions.cs`, each having a plain overload and a `version =>`
overload for the rebuild path.

### Two consequences

**The store cache key is `(tenantId, rebuildVersion)`, not `tenantId`.** The
`version =>` overloads mean the shadow path builds stores too. Getting this
wrong makes a rebuild write into the live version.

**The batch path goes from one `LoadManyAsync` to one per tenant present in the
batch.** In single-tenant mode every `TenantId` is null, so there is one group
and behaviour is byte-identical. Under multi-tenant load it is a real
throughput change, and `benchmarks/Alberto.Dcb.Benchmarks` should pick it up.

## SP0, SP3, SP5, SP6

**SP0** adds `coverlet.collector` to the test project and
`--collect:"XPlat Code Coverage"` to `.github/workflows/ci.yml`. It exists to
give the other sub-projects a baseline to move, and it is first because it is
half an hour of work.

**SP3** covers the CLI. The output formatters (`HumanOutput`, `JsonOutput`),
`ConnectionResolver`, `ConfigFileFinder` and `ShardResolver` are pure and test
at unit level. The command types test against a real database through the
existing `PostgresCluster`, because what they are for is issuing correct SQL.
`RewindAsync` and `RetryByRewindAsync` get particular attention: they are the
only paths that move a checkpoint backwards.

**SP5** adds test projects for `apps/Alberto.Orders` and `apps/Alberto.Payments`
that consume `Alberto.Dcb.Testing` exactly as a customer would — via package
reference semantics, not project-internal shortcuts. Note that
`.github/workflows/ci.yml` currently builds only `tests/Alberto.Dcb.Tests`, and
excludes apps projects because they need the Aspire workload; SP5 must either
install that workload in CI or structure the app test projects so they do not
require it.

**SP6** is the mechanical sweep: 1,047 `Assert.*` calls against 550 `.Should()`
calls settle on one style, and the 25 `[Theory]` attributes out of 934 grow
where a test is visibly a table of cases. It goes last because it touches
everything.

## Testing this work

Each sub-project is verified by the existing suite plus its own additions. The
suite runs in 18.5 seconds, so it runs before every commit, and nothing is
committed red.

Two sub-projects need verification beyond "tests pass":

- **SP4** must be checked under the load that surfaced the defects: five
  concurrent test processes, twenty runs, zero failures of the two named tests.
- **SP1b** must not change what any migrated test asserts. Its commits should be
  reviewable as pure substitution.

`Regression/DiscoveredIssuesTests.cs` pins eleven current-but-wrong behaviours
and is not trait-tagged, so fixing any of those bugs turns the suite red for
reasons that look like regressions. SP6 should tag that class so intentional
behaviour changes are distinguishable from breakage.

## Branch and commit strategy

One branch per sub-project off `main`, one PR per sub-project, matching the
existing merge-commit workflow. Commit at every green point.
