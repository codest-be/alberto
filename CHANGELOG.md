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

**`EventEnvelopeExtensions.ParseEvent<T>` removed** — the extension method bypassed registered
upcaster chains. It carried `[Obsolete]` for part of this cycle and is now deleted; with no
public members left, `EventEnvelopeExtensions` itself is `internal`. Inject `EventSerializer`
and call `serializer.Deserialize(envelope)` instead.

**`IStateStore<TState>.LoadManyAsync` return type changed** — now returns
`Task<IReadOnlyDictionary<TKey, TState?>>` (was `Task<Dictionary<TKey, TState?>>`).

**`IEventProcessor.IsActive` and `IsRebuilding` are now getter-only** — remove any code that
sets them directly; the framework sets them.

**Record positional constructors removed** — `DeadLetterEntry`, `ProcessorExecutionOptions`,
and `ProjectionStoreContext` no longer expose positional constructors. Switch to the
named-property form: `new DeadLetterEntry { ... }`.

**`ExternalMessage` and `OutboxEntry` gain routing fields** — `Destination` (required,
`string`) and `RoutingHint` (optional, `string?`) are new properties. Existing construction
sites that create these records without named properties will fail. **Run migration 027
before deploying the new binary** — it adds the two columns to `alberto_outbox_entries`.

**`IEventEnvelope.CreatedAt` and admin record timestamps changed to `DateTimeOffset`** —
`IEventEnvelope.CreatedAt`, `ProcessorInfo.UpdatedAt`, `CheckpointInfo.UpdatedAt`,
`DeadLetterInfo.FailedAt`, `ProjectionState.UpdatedAt`, `AdminTenantLease.ExpiresAt`, and
`ActiveProcessorLease.ExpiresAt` were `DateTime`/`DateTime?` and are now `DateTimeOffset`/
`DateTimeOffset?`.

**Leading-underscore tag concepts reserved** — `EventTag` and `[Tag]` now throw
`ArgumentException` for any concept starting with `_`. Alberto writes `_version:N` on every
event to record its schema version, and the whole prefix is reserved so later framework tags
cannot collide with application ones. Application tags using a leading underscore must be
renamed.

**Metric tag shapes for sharded modules** — the `module` dimension of `alberto.dead_letters`,
`alberto.retries`, and `alberto.processor.lag` no longer carries a combined
`"{moduleKey}#{shardId}"` string. It now splits into two dimensions: `module` and `shard`.
Custom dashboards built on the old combined value need updating.

**`tenant.id` removed from the tenant-lock counters** — `alberto.tenant_locks_acquired` and
`alberto.tenant_lock_failures` are now tagged by `consumer.id` only. A tenant id is unbounded, and
every distinct tag combination is a time series the SDK holds for the life of the process, so the
old shape cost one series per tenant per replica per counter. Queries that grouped or filtered by
`tenant.id` need rewriting — aggregate by `consumer.id`, or use traces for the per-tenant question.

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

**Outbox transport lifecycle ownership** — once Alberto calls `IMessageTransport.StartAsync`, it
now calls `StopAsync` even if startup throws after partial initialization. Cleanup is bounded to
30 seconds, and one transport instance reused across `WithOutbox` registrations has one shared
lifecycle. Transport implementations must tolerate partial-start cleanup, finish promptly when
cancelled, and support concurrent publishing when an instance is shared. Polling-store failures
now fault the relay instead of being retried indefinitely.

**Telemetry no longer emits tag values or exception text by default** — the `event.appended`
span event carried `event.tags` as the full `order:8f21,customer:4471` list, and the append and
consume paths set `exception.message` / `exception.stacktrace` as plain span attributes. A DCB
tag value is a domain identifier, and an exception message carries whatever the thrower put in
it (Npgsql's include the failing SQL), so a trace exporter was receiving business data either
way. `event.tags` now lists the distinct *concepts* — `order,customer` — which is what identifies
the boundary the append was checked against; opt the ids back in with
`TelemetryOptions.RecordEventTagValues = true`. Exceptions go through `Activity.AddException`,
which puts them on an exception span event that a collector can drop or scrub as a unit.
Trace queries reading `event.tags` or the `exception.*` span attributes need updating.

**`BatchedEfProjection<TDbContext, THandler>` and `IEfBatchHandler<TDbContext>` removed** — a
public pair with no registration path (there was never an `AddBatchedEfProjection`), so reaching
them meant hand-registering into keyed DI, which produced no `RebuildableProjection`: a rebuild
would skip the processor entirely and leave it permanently diverged from every projection that
promoted. It also had no `LastProcessedPosition` guard, so a crash between its `SaveChanges` and
the checkpoint write replayed the whole batch into the handler. Use `AddEfProjection`, which is
idempotent per document, participates in rebuilds, and is itself an `IBatchableProcessor` —
a batch still becomes one `SaveChanges` per tenant run.

**`IDeadLetterStore` split — retry claims moved to `IClaimableDeadLetterStore`** —
`ClaimRetryRequestedAsync`, `CompleteRetryAsync` and `AbandonRetryAsync` are no longer members of
`IDeadLetterStore`. They required an atomic claim-and-fence that a store backed by a file, a log,
or an HTTP endpoint cannot honour, so every such store had to implement three methods it could
only throw from. They now live on the optional `IClaimableDeadLetterStore`, which both shipped
stores implement. A store that does not implement it still records, counts, reads and clears dead
letters; what it loses is the automatic retry loop, and the host now logs a warning naming the
store type at startup rather than leaving an operator watching a retry flag that never clears.
The conformance suite splits the same way: `DeadLetterStoreSpecification` keeps the core
requirements, `ClaimableDeadLetterStoreSpecification` adds the claim ones.

**`AddEfProjection` on a tenant-enabled module now requires a uniqueness declaration** — an EF
projection is keyed by `(DocumentId, RebuildVersion)`, the only two columns `IProjectionEntity`
has, so neither the async store nor the inline path can add a tenant predicate. On a module that
declared `.WithTenancy()` that is only correct if two tenants can never produce the same document
id. Registration now refuses the combination with `ALB0027` unless the call passes
`documentIds: EfDocumentIdUniqueness.AcrossTenants`. Single-tenant modules are unaffected.

**`EventSerializer.Deserialize` refuses an uncovered schema-version gap** — reading an envelope
stored below the version its CLR type declares now throws `InvalidOperationException` instead of
deserializing the older payload straight into the current shape, where every member added since
would take its CLR default. `ALB0018` already caught this at startup, but only on the DI path; a
serializer built by hand never met the validator. Waive the gap at the declaration site with
`[EventType("slug", Version = N, UpcastingNotRequired = true)]` when the bump only added optional
members whose defaults are already right for older events — `ALB0018` honours the same flag.

**PostgreSQL append advisory-lock key widened to 64 bits** — the append lock moved from
`pg_advisory_xact_lock(1, hashtext(key))` to `pg_advisory_xact_lock(hashtextextended(key, 0))`.
No schema change, but **two application versions appending to the same database concurrently take
locks in different key spaces and do not serialize against each other**, so the DCB conflict check
is unprotected for the length of the overlap. Drain or stop the old version before starting the
new one; see [UPGRADING.md](UPGRADING.md) for the rollout note.

**The admin surface does not ship in 1.0** — `Alberto.Dcb.Admin` is no longer published to nuget.org,
and the PostgreSQL implementation of it moved out of the `Alberto.Dcb.Postgres` package into a new,
also-unpublished `Alberto.Dcb.Postgres.Admin`. `IAdminReader`/`IAdminOperator` are the contract the
admin front doors on `feature/admin-surface` are built on, and shipping them at 1.0 would freeze that
abstraction under semver before anything that consumes it exists. Both projects still build, are in
the solution, and are referenced by `tools/Alberto.Cli` — the operator CLI is unaffected, and so is
every consumer that only ever used the CLI.

The concrete break is for anyone referencing `PostgresAdminDataAccess`, `PostgresAdminOperator` or
`AddAlbertoPostgresAdmin` from the `Alberto.Dcb.Postgres` **package**: those 33 members are gone from
it. Their namespace is unchanged (`Alberto.Dcb.Postgres`), so no `using` needs editing, but the types
now live in an assembly you can only get by project reference. Build the CLI from source, or wait for
the admin surface to be unparked after 1.0.

**Delivered outbox entries are now deleted after 7 days by default** — `WithOutbox` registers an
`OutboxRetentionService` that sweeps hourly and deletes `delivered` entries older than
`deliveredRetention`. Nothing removed them before, so **the first sweep after upgrading faces every
delivered entry the table has ever held**; on a large table that one DELETE can be long. Purge it
once from the CLI during a quiet window first (`alberto ops outbox purge --before <timestamp>`), or
start with a wide `deliveredRetention` and walk it down. Pass `Timeout.InfiniteTimeSpan` to keep
delivered entries forever — do that if the table is your integration audit trail. Only `delivered`
entries are ever eligible; `pending`, `processing` and `failed` are work, not history, and are never
removed by age. **Run migration 034 before deploying the new binary** — it adds the partial index on
`delivered_at` the sweep needs, without which the DELETE is a sequential scan.

### Changed

- **Breaking — everything renamed from `Alberto.Dcb.*` to `Alberto.*`.** Package IDs, assemblies,
  namespaces and directories all moved together. `Alberto.Dcb` is now `Alberto`,
  `Alberto.Dcb.Postgres` is now `Alberto.Postgres`, and so on for all ten packages. Types whose
  names contain `Dcb` — `DcbQuery`, `DcbModuleBuilder` — are unchanged.
- **Breaking — `Alberto.Dcb.Postgres.Messaging` is now `Alberto.Messaging.Postgres`.** Package
  segments now read feature-first, matching `Alberto.Testing` / `Alberto.Testing.Xunit` and .NET
  convention generally.
- **Breaking — the command pipeline moved out of the core namespace.** `AlbertoStore`,
  `CommandPipeline<T>`, `BoundPipeline<T,S>`, `UnboundPipeline<T,S>`, `BoundDecision`,
  `UnboundDecision`, `DeciderExtensions` and `AlbertoStoreBuilderExtensions` now live in
  `Alberto.Commands`. `Result`, `Decision` and `Problem` are core types and stay in `Alberto`.
  Call sites need `using Alberto.Commands;`.
- **Breaking — existing PostgreSQL stores cannot be upgraded across this rename.** DbUp records
  each executed migration in `schemaversions` by its embedded-resource name, and those names
  carry the assembly name. Every script's recorded name changed, so DbUp sees all 34 as pending
  and a replay against a migrated database fails. Drop and recreate any store created before
  this version. No bridging script is provided; there were no published packages and no external
  consumers at the time of the rename.
- **Breaking — the OpenTelemetry meter and ActivitySource are now named `Alberto`**, not
  `Alberto.Dcb`. Update any collector filter, dashboard or alert that matches on the old name.

### Added

- **A backup and recovery page.** [docs/backup-and-recovery.md](docs/backup-and-recovery.md) states
  which tables are truth and which are derived, and what restoring them from different points in
  time silently invalidates. The failure it exists to prevent: checkpoint writes are monotonic
  (`GREATEST`), so a checkpoint restored *ahead* of the log head never rewinds and never errors — the
  processor waits for a position the log has not reached again yet and skips everything in between,
  with nothing logged. `alberto ops checkpoint set` is the only way back. Alberto still ships no
  backup tool of its own; `pg_dump` and PITR are the whole mechanism, and this documents the part a
  generic database backup policy does not know about.
- **The outbox no longer grows forever, and its ordering guarantee is written down.**
  `WithOutbox` gains `deliveredRetention` (default 7 days, `Timeout.InfiniteTimeSpan` to disable)
  and `retentionSweepInterval` (default 1 hour), honoured by a new `OutboxRetentionService`. It is
  deliberately a separate hosted service rather than a step on the relay's loop: a purge that takes
  seconds would otherwise be seconds in which nothing is published. It waits out a full interval
  before its first sweep, several replicas sweeping at once is safe, and a failed sweep is logged
  and retried rather than faulting the host. `alberto ops outbox purge --before <timestamp>` does
  the same delete on demand and records an `admin-outbox-purged` audit event;
  `IAdminOperator.PurgeOutboxAsync` is the operation behind it. Migration 034 adds the partial index
  on `delivered_at` that keeps the sweep off a sequential scan.
  Separately, [docs/reactors-and-outbox.md](docs/reactors-and-outbox.md#ordering-there-isnt-any) now
  states plainly that outbox delivery is **unordered** — `created_at` is transaction-start time with
  no tiebreaker, retries re-deliver late, and `FOR UPDATE SKIP LOCKED` hands concurrent relays
  disjoint batches — and points at `ExternalMessage.RoutingHint` as the per-entity ordering hook for
  transports that have partition keys, message-group ids or routing keys. No behaviour changed
  there; it was true before and undocumented.
- **A conflict that outlives `Commit`'s retries now reaches the client as a coded error.**
  `Commit` retries a `DcbConflictException` up to its attempt limit and then rethrows; the example
  slices' `OrThrow` awaited that bare, so a boundary that stayed contended surfaced as an unhandled
  exception — a 500 with no code on it, which is the worst error on the documented happy path.
  `OrThrow` now catches it and raises the same `Problem` a `TryCommit` would have returned, so
  `Handle → Load → Decide → Commit → OrThrow` stays the shape a slice is written in and
  `TryCommit` stays what it was for: branching on the failure rather than reporting it.
  `DcbConflictException` gains `ProblemCode` (`"dcb.conflict"`) and `ToProblem()` so both paths
  render one shape and callers branch on a constant. `Problem.Details` now reach GraphQL as error
  extensions instead of being dropped, which is how `expectedPosition` and `conflictingPosition`
  get to a client deciding whether to retry.
- **Every destructive CLI command is now recorded.** Every mutation in `alberto ops` routes
  through `IAdminOperator`, which appends an admin event to `alberto_events` in the same
  transaction as the change. Previously `checkpoint set`, `checkpoint reset`, `checkpoint rename`,
  `dead-letters dismiss`, `dead-letters retry` and `tenants release` reached past it to the
  underlying stores and left no trace; only `retry-rewind` and the three rebuild verbs were
  audited. `IAdminOperator` gains `RenameCheckpointAsync` and `MarkDeadLettersForRetryAsync` (and
  two matching event types, `admin-checkpoint-renamed` and `admin-dead-letters-marked-for-retry`)
  so the two commands that had no operator-level equivalent now have one. The recorded operator id
  is the CLI's OS user name — attribution for a cooperating team, not authentication; database
  credentials remain the access control. See
  [docs/operations.md](docs/operations.md#what-a-mutation-records).
- `TelemetryOptions.RecordEventTagValues` (default `false`) — opts append spans back into
  carrying tag values alongside tag keys, for a development environment or a collector inside
  the same trust boundary as the database.
- **Public API tracking on every shipped package** — each project under `src/` now carries
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, and `Microsoft.CodeAnalysis.PublicApiAnalyzers`
  fails the build on a public symbol that is not declared in them. Adding or removing public API
  is now a reviewable diff rather than something that can slip out in a patch release.
- **Backend conformance suite additions** (`Alberto.Dcb.Testing.Xunit`) — three new requirements
  for third-party `IEventStoreBackend` implementations: `EventType.Version` must be derived from
  the stored `_version` tag on read; malformed JSON must be rejected at append; and a payload
  containing a NUL byte must be rejected rather than silently truncated. Payload round-trip is
  asserted for semantic JSON equality, not byte equality, so a backend storing into `jsonb` is
  not required to preserve key order or insignificant whitespace.
- `ALB2001` — a Roslyn analyzer shipped inside `Alberto.Dcb.Commands` that warns when a command
  pipeline is built and then discarded, which appends nothing. Referencing the package is all that
  is needed. Assigning the pipeline to a variable, or writing `_ =`, is not reported. See
  [docs/configuration.md](docs/configuration.md#compile-time-codes-alb2xxx).
- `ALB0026` — `AddAlberto` now refuses a module key that was already registered, instead of
  overlaying the first module's options and starting a second set of control loops that race on
  its checkpoint. The rejected call registers nothing.
- `ALB0027` — `AddEfProjection` refuses a tenant-enabled module unless the call declares
  `EfDocumentIdUniqueness.AcrossTenants`. Raised from the deferred registration callback, so
  `.WithTenancy()` is seen whether it is chained before or after the projection.
- `IClaimableDeadLetterStore` and `ClaimableDeadLetterStoreSpecification` — the optional
  claim-and-fence capability split out of `IDeadLetterStore`, and its conformance suite.
- `EfDocumentIdUniqueness` — what a caller guarantees about the document ids an EF projection
  declaration produces on a tenant-enabled module.
- `EventTypeAttribute.UpcastingNotRequired` — states that events stored at an older version of
  this type deserialize correctly into the current shape without an upcaster. Honoured by both
  `ALB0018` and `EventSerializer.Deserialize`. Defaults to `false`, which is the safe answer.
- `DcbConflictException(string, long, long, DcbQuery, Exception)` — keeps the provider exception
  *and* the conflict details, for a backend that learns of a conflict from its database.
- `ILogger` parameter on `ConsumeMiddlewares.RetryAndDeadLetter` (optional, matching the batch
  overload) — surfaces a dead-letter write that failed and was dropped.
- **Extension-point contract freeze** — `ExtensionPointContractTests` pins the abstract member set
  of every interface Alberto expects to be implemented outside this repository, so a member added
  after 1.0 must ship with a default implementation or move to its own optional interface. See
  [CONTRIBUTING.md](CONTRIBUTING.md).
- Event schema versioning: `[EventType("slug", Version = N)]`, the framework-managed `_version:N` tag,
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

- The operator CLI is no longer published as a NuGet tool. It had not packed since the
  `IsPackable` default changed, and it is not part of the 1.0 surface. Run it from the repo with
  `dotnet run --project tools/Alberto.Cli`.
- `BatchedEfProjection<TState>` and `IEfBatchHandler<TState>` — see Breaking changes above.
- Wildcard concept-tag boundaries removed from the query DSL and the PostgreSQL backend.
  Use explicit per-concept tag boundaries instead.
- `TenantEventStoreDecorator.StreamAll` now throws `InvalidOperationException` in multi-tenant mode
  (was silently returning events for all tenants).
- `BufferedCheckpointStore` (was internal and never constructed; `CachingCheckpointStore` was
  always the live implementation).
- `PostgresEventStore` and `InMemoryEventStore` concrete types (merged into `EventStore`).

### Fixed

- A failed dead-letter write took the whole processor down with it. The store write at the end of
  the retry chain was unguarded on the single-event path, so a transient database blip threw out
  of the middleware chain, faulted the processor, and held the checkpoint — re-delivering every
  healthy event in that window on the next pass, and on every restart after it. The write is now
  retried three times and then dropped with an error log naming the event and processor: losing
  one dead-letter entry is strictly safer than re-delivering healthy events forever. Cancellation
  still propagates, because a shutdown is not a failed write. Both the single-event and batch
  middlewares route through the same helper.
- Every PostgreSQL DCB conflict reported `ConflictingPosition` and `ExpectedPosition` as `-1` and
  `Query` as `DcbQuery.Empty` — which renders as `*`, indistinguishable from a real conflict
  against an all-events query. The backend threw the two-argument `DcbConflictException(message,
  inner)` overload, discarding details it was holding. It now reports the expected position and
  query it was given, parses the conflicting position out of the server's `RAISE EXCEPTION`
  message, and keeps the server's own wording — which names *which* arm of the boundary matched —
  alongside them.
- The PostgreSQL append advisory lock hashed its key to 32 bits, so two unrelated tenants'
  appends could share one lock: by the birthday bound, ~0.6% chance of some colliding pair at 10k
  tenants, 50% at 77k. Never a correctness problem — a shared lock over-serializes, it never
  under-serializes — but an invisible throughput cliff on the headline multi-tenant feature, with
  no error, no log line and nothing in telemetry to point at it. The key is now 64-bit
  (`hashtextextended`), which moves the same bound to under 1 in 10¹⁰ for a million tenants.
- Inline EF projections neither filtered nor stamped `RebuildVersion`. Register the same entity
  as both inline and async, start a rebuild of the async side, and the table holds `(docId, v1)`
  and `(docId, v2)` at once — the unfiltered load returned both and threw a duplicate-key
  `ArgumentException` out of the caller's `AppendAsync`, for the entire rebuild window. Reads are
  now filtered by the live version and writes stamp it, exactly as `EfStateStore` does, with the
  version resolved once per attempt so a promotion landing mid-write cannot split the load, the
  upserts and the deletes across two versions.
- The in-memory backend accepted payloads the PostgreSQL backend rejects and returned them
  unchanged, so a suite that passed in memory could fail against a real database. It now
  validates `EventData` as JSON, rejects a NUL byte in the payload or in metadata (`jsonb`
  accepts neither), and re-emits the payload in canonical form — duplicate keys collapsed,
  keys sorted, insignificant whitespace dropped — the way `jsonb` does.
- The in-memory backend aliased the caller's tag and metadata collections into the stored
  envelope, so mutating the collection after appending changed what the store returned.
  Both are now copied, matching PostgreSQL, which rebuilds them from the row it read.
- Dead-letter middleware now preserves tenant identity, tags, metadata, and the original event
  timestamp; PostgreSQL dead-letter and outbox adapters derive tenancy from the migrated schema
  instead of trusting an optional caller flag.
- `DcbQuery` and built projection declarations now snapshot their input collections, preventing
  caller or builder mutations from changing a live query/processor declaration.
- The CLI now rejects malformed, unknown, or partial shard configuration and applies one
  non-interactive-safe confirmation gate before destructive fan-out operations.
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
