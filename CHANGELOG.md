# Changelog

All notable changes to the Alberto NuGet packages are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

Breaking changes have detailed migration guides in [UPGRADING.md](UPGRADING.md).

---

## [Unreleased] — working toward v1

### Breaking changes

For full migration guidance see [UPGRADING.md](UPGRADING.md).

**net9.0 target removed** — all seven core libraries now ship `net10.0` only. Projects that
multi-target net9.0 must upgrade to net10.0 or pin to an earlier beta.

**Public surface narrowed** — `FencingContext`, `ConsistentHashRing`, `FunctionalReactor<T>`,
and `DeadLetterRetryLoop` are now `internal`. `AlbertoStore.FoldWithPosition<TState>` and
`ReconstituteWithPosition<TState>` are `internal`; use `AlbertoStore.Fold<TState>` and
`Reconstitute<TState>` instead. `IReact<TEvent>` and `AsyncReactor<TReactor>` are deleted;
use `ReactTo<TEvent, THandler>(h => h.Method)` on the module builder instead.

**`IStateStore<TState>.LoadManyAsync` return type changed** — now returns
`Task<IReadOnlyDictionary<TKey, TState?>>` (was `Task<Dictionary<TKey, TState?>>`).

**`IEventProcessor.IsActive` and `IsRebuilding` are now getter-only** — remove any code that
sets them directly; the framework sets them.

**Record positional constructors removed** — `DeadLetterEntry`, `ProcessorExecutionOptions`,
and `ProjectionStoreContext` no longer expose positional constructors. Switch to the
named-property form: `new DeadLetterEntry { ... }`.

**`ExternalMessage` and `OutboxEntry` gain routing fields** — `Destination` (required,
`string`) and `RoutingHint` (optional, `string?`) are new properties. Existing construction
sites that create these records without named properties will fail. **Run migration 025
before deploying the new binary** — it adds the two columns to `alberto_outbox_entries`.

**`IEventEnvelope.CreatedAt` and admin record timestamps changed to `DateTimeOffset`** —
`IEventEnvelope.CreatedAt`, `ProcessorInfo.UpdatedAt`, `CheckpointInfo.UpdatedAt`,
`DeadLetterInfo.FailedAt`, `ProjectionState.UpdatedAt`, `AdminTenantLease.ExpiresAt`, and
`ActiveProcessorLease.ExpiresAt` were `DateTime`/`DateTime?` and are now `DateTimeOffset`/
`DateTimeOffset?`.

**`_v` tag concept reserved** — `EventTag`, `[Tag]`, and `TagPattern` now throw
`ArgumentException` when the concept name is `_v`. Application tags using that name must be
renamed.

**Metric tag shapes for sharded modules** — the `module` dimension of `alberto.dead_letters`,
`alberto.retries`, and `alberto.processor.lag` no longer carries a combined
`"{moduleKey}#{shardId}"` string. It now splits into two dimensions: `module` and `shard`.
Custom dashboards built on the old combined value need updating.

**Command pipeline reshape** — `Persist`/`PersistUnconditionally` renamed to `Commit`/`CommitUnconditionally`.
`NoValidation()` removed (validation was always optional). `Decide` is now synchronous; use the new
`Enrich` stage for async work before the consistency boundary. `LoadUnder` added for the case where
the boundary is only discoverable during the load itself. `AlbertoStore` is now registered as a
keyed scoped service (`GetRequiredKeyedService<AlbertoStore>(moduleKey)`). Two new terminals:
`TryCommit` (returns a failed `Result` rather than throwing) and `RetryOnConflict(n)`.

**`AddAlbertoStore` removed** — use `WithEventsFrom(assembly)` inside the `AddAlberto` callback
instead.

**Deprecated projection and decision APIs removed** — `DecisionResult<TEvent>`, the
`Projection<TState>` base class and all associated reflection-dispatch infrastructure
(`IProject<,>`, `ProjectionDispatcher<TState>`, `AsyncProjection`, `InlineProjection`,
`EfInlineProjection`) have been deleted. Use `DeclareProjection.For<TState>(...)` and
`Decision`/`Problem` instead.

**Declarative configuration pipeline (0.x → 1.0)** — `DcbModuleBuilder.Services` removed.
`Action<TOptions>` mutators replaced by `Func<TOptions, TOptions>` (`with`-expression style).
`ControlLoopBuilder` deleted; use `WithControlLoop(o => o with { ... })`. `WithMiddleware` /
`WithBatchMiddleware` replaced by module-level `AddConsumeMiddleware` / `AddBatchConsumeMiddleware`.
`ReactTo<TEvent, THandler>` now derives its processor id from the handler type name when
`processorId` is omitted — audit existing checkpoint keys before deploying. `Checkpoints:OrphanPolicy`
defaults to `Strict` outside a `Development` environment.

**Projection rebuild cycle** — projection state is now versioned. `AddProjection` takes a
`ProjectionStoreContext` (not `IServiceProvider`). EF projection entities require a
`(DocumentId, RebuildVersion)` composite key. `IProjectionStateClearer.ClearAsync` →
`ClearVersionAsync(int, ct)`. `alberto ops rebuild` gained `start|status|promote|abort`
subcommands.

**Outbox claim leases** — `IOutboxStore.GetPendingAsync` replaced by `ClaimPendingAsync`;
completion requires an `OutboxClaim` token. Migration 016 adds claim columns to the outbox table.

**Command decisions require an observed DCB position** — `DecidedPipeline.Persist(DcbQuery, ct)`
replaced by `Persist(DcbQuery, long expectedPosition, ct)`.

**Core and operator correctness hardening** — PostgreSQL schema names restricted to
`^[a-z_][a-z0-9_]*$` (fail `ALB1005` otherwise). `GetTenantLeasesAsync` replaced by
`GetTenantLeaseInventoryAsync`. `PostgresAdminDataAccess.SetCheckpointAsync`/
`ResetCheckpointAsync` replaced by atomic `RenameCheckpointAsync`.

**`ReactTo` arity-ladder overloads removed** — six typed-dependency overloads deleted;
use the factory form `ReactTo<TEvent>(Func<IServiceProvider, Func<TEvent, ct, Task>>, ...)` instead.

**`IEventStoreBackend` method renames + `IEventStoreHeadBackend` split** — `Stream`/`StreamAll`/
`Append`/`GetLastPosition` renamed with the `Async` suffix. `GetPositionsAsync` and
`GetStableHeadAsync` moved to a new `IEventStoreHeadBackend` interface.

**`[Tag]` no longer valid on bare primary-constructor parameters** — use `[property: Tag(...)]`.

### Added

- Event schema versioning: `[EventType("slug", Version = N)]`, the framework-managed `_v:N` tag,
  `UpcasterRegistry`, and the `DeclareUpcaster.For<T>(...).From<TOld>(...).Build()` fluent API.
  See [docs/events.md](docs/events.md) for the full guide and known limits.
- `ExternalMessage.Destination` and `ExternalMessage.RoutingHint` routing fields on outbox messages.
- `CachingCheckpointStore` now implements `ICheckpointInventory` (flushes in-memory state before listing).
- `[Experimental("ALB9001")]` on all public sharding types — makes the experimental status visible at compile time.
- EF projection processor ID validation: `AddEfProjection` rejects IDs containing `:` or `#` and reserved words.
- `ALB0017` validation code — shared in-memory backend combined with `.WithTenancy()` (singleton/scoped lifetime conflict).
- `WithEventsFrom(assembly)` replaces `AddAlbertoStore` (chained from the module builder).
- `TryCommit` and `RetryOnConflict(n)` terminals on the command pipeline.
- `Enrich` pipeline stage for async enrichment before the consistency boundary.
- `LoadUnder` for caller-controlled boundary + state loading in one step.
- Zero-downtime projection rebuilds via `WithRebuilds()` + `alberto ops rebuild start|status|promote|abort`.
- `InlineProjectionExhaustedException` wraps EF inline projection retry exhaustion.
- `IEventStoreHeadBackend` interface extracted from `IEventStoreBackend`.
- `IEventStoreConfigurator` extracted from `IEventStore` for setup-time registration methods.
- `IAlbertoBackendDescriptor` for backend-specific descriptor extensions.
- Multi-database tenant sharding (`WithTenancy(t => t.AcrossPostgresDatabases(...))`);
  shard management via `alberto shards list|where|assign`.
- `alberto ops checkpoint rename` for safe in-place checkpoint key migration.
- `ProcessorId.For<THandler>()` derives processor ids from type hierarchy + `[ProcessorId]` attribute.
- `Checkpoints:OrphanPolicy` configuration key (`Strict`, `Warn`, `Ignore`).
- `ProjectionVersions.LiveVersion` for reader-side version resolution.
- `TimeProvider` seam throughout the async machinery (control loop, dead-letter retry, caching checkpoint store, rebuild sweep).
- Rebuild version reclaim after a configurable grace period (`ReclaimGracePeriod`).
- Stable-head query index on PostgreSQL backend.
- Fenced checkpoint writes on lease generation.
- Projection wait in subscription helpers instead of fixed sleeps.
- CI code coverage collection.

### Removed

- Wildcard concept-tag boundaries removed from the query DSL and the PostgreSQL backend.
  Use explicit per-concept tag boundaries instead.
- `TenantEventStoreDecorator.StreamAll` now throws `InvalidOperationException` in multi-tenant mode
  (was silently returning events for all tenants).
- `BufferedCheckpointStore` (was internal and never constructed; `CachingCheckpointStore` was
  always the live implementation).
- `PostgresEventStore` and `InMemoryEventStore` concrete types (merged into `EventStore`).

### Fixed

- Control loop faults and holds its checkpoint on a pipelined handler failure that coincides
  with shutdown (previously the checkpoint could advance past the failed event).
- Schema SQL injection vector via unquoted schema identifier interpolation (now validated against
  an allowlist and quoted at the DDL/DML call sites).
- Tenant context not scoped to the active request on multi-tenant `AlbertoStore`.
- Stale outbox relay no longer silently overwrites a newer claim on completion.
- `EfStateStore.ApplyChangesAsync` now commits atomically and surfaces `ConcurrencyConflictException`
  after bounded retries (previously could silently swallow write failures).
- Dead-letter reads were not scoped to the active tenant.

---

## [0.1.0-beta]

Initial beta release. Core DCB event store abstractions, PostgreSQL backend, in-memory
backend, command pipeline, EF Core projection support, transactional outbox, and
OpenTelemetry instrumentation.
