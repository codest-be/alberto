# Alberto 1.0 Configuration DX — SDD progress

Plan: docs/superpowers/plans/2026-07-24-alberto-configuration-dx.md
Branch: claude/alberto-release-dx-dee517
Merge base: 8e38d68

## Completed tasks

## Minor findings deferred to final review

Task 1: complete (commits a22ed4f..f1f2796, review clean)

### Watch items (for final whole-branch review)
- Directory.Packages.props:13 pins Microsoft.Extensions.Options at 9.0.0 while the
  Configuration.Binder / Options.ConfigurationExtensions siblings are 10.0.7. Alberto.Dcb
  currently resolves Options to 10.0.7 transitively on both TFMs, so no live conflict —
  but the stale pin bites any project that references Options directly.
- Task 1 Minor (deferred): Overlay<TOptions,TOverrides> has no new() constraint on
  TOverrides though ConfigurationBinder requires a parameterless ctor
  (src/Alberto.Dcb/Configuration/IAlbertoOverrides.cs:38).
- Task 1 Minor (deferred): TelemetryOptions defaults (Enabled, RecordEventPayloadSize)
  are not pinned by a defaults test.
Task 2: complete (commits f1f2796..c6acc06, review clean)
  - Note: AlbertoValidationFailure.cs was created in Task 2 per the brief's forward-reference
    note; verified verbatim against Task 3's spec (record + Format + Describe signatures match).
    Task 3 skips its Step 3.
  - Task 2 Minor (deferred): AlbertoValidationReport.Describe takes IReadOnlyList while
    IAlbertoBackendDescriptor.Validate returns IEnumerable, forcing .ToList() at aggregation
    sites. Both signatures are plan-specified.
Task 3: complete (commits c6acc06..28bafc1, review clean)
  - Task 3 Minor (deferred): unused `using System.Collections.Immutable;` in
    tests/Alberto.Dcb.Tests/Configuration/AlbertoModuleValidatorTests.cs:185
  - Task 3 Minor (deferred): ALB0006 guard's IsNullOrWhiteSpace || Any(IsWhiteSpace) is
    redundant; IsNullOrEmpty || Any(IsWhiteSpace) reads clearer
    (src/Alberto.Dcb/Configuration/AlbertoModuleValidator.cs:160)
  - Task 3 Minor (deferred): ALB0004 theory rows exercise only PollingInterval and BatchSize;
    HeadRefreshInterval and HeadWindowSize are validated but untested

### Pre-existing repo condition (decide before Task 12)
`dotnet build` at the SOLUTION level already fails at the merge base (8e38d68), before any of
this branch's work: apps/Alberto.Orders/.../OrderSummaryEfProjection.cs (7x CS0407 wrong Apply
return type) and apps/Alberto.Payments/.../*Projection.cs (CS1061 ProjectionDeclarationBuilder
has no 'Handles'). Verified by building the merge base in a scratch worktree: 22 errors there.
CI (.github/workflows/ci.yml) only builds+tests tests/Alberto.Dcb.Tests, so this does not gate
green. But Task 12 Step 5 expects a clean `dotnet build`, and Task 12 Steps 3+6 migrate
OrdersModule.cs and smoke-test the AppHost — both need apps/ to compile. Needs a scope decision
from the user at the Task 11 -> 12 boundary.
Task 4: complete (commits 28bafc1..08f4e54, review clean)
  - Adjudicated a plan self-contradiction: brief's Describe qualifies by ALL declaring types but
    its test helpers were nested in the fixture and asserted unqualified names. Kept the
    production code, moved the test helpers to namespace level, added a collision test pinning
    Orders.SummaryHandler != Invoices.SummaryHandler. (da2e2ac introduced a skip-outermost rule;
    08f4e54 reverted it.)
  - Task 4 Minor (deferred): IsNullOrWhiteSpace || Any(IsWhiteSpace) redundant, same shape as the
    Task 3 minor (src/Alberto.Dcb/Configuration/ProcessorIdAttribute.cs:69)
  - Task 4 Minor (deferred): test helpers are now namespace-scoped internal types, so a second
    test file in Alberto.Dcb.Tests.Configuration could collide on names like Outer.
  - Residual, accepted: two same-named TOP-LEVEL handlers in different namespaces still derive
    the same id; ALB0002 catches it at startup with a [ProcessorId] remedy.

### AFK directive (received during Task 4)
User is away: execute all remaining tasks autonomously, then PR -> green -> merge to main, and
leave a summary. Scope decision for Task 12 must therefore be made without asking. Default
chosen: pre-existing apps/ build breakage stays out of scope; CI (test project only) is the
green gate. Revisit with data at Task 12.
Task 5: complete (commits 08f4e54..8173cc0, review clean — opus reviewer, Approved)
  - Reviewer confirmed: no I/O at composition, order-independence proven by test,
    ValidateOnStart surfaces OptionsValidationException at host start, overlay precedes validation.
  - Reviewer endorsed my adjudication: control-loop auto-add removal is temporary, Task 6 restores it
    via deferred ControlLoopRegistration.Register. ControlLoopConfigured retained at DcbModuleBuilder.cs:39.
  - ⚠️ item (validator ALB0001 message strings live in unchanged Task 3 code) — CLEARED by me:
    StartupValidationTests pass against the actual validator, so the strings match.
  - Deferred Minor findings (for final whole-branch review triage):
    M5.1 ControlLoopBuilder.cs file-level `#pragma warning disable CS0618` guards a single access site;
         narrow it to a disable/restore pair. Same for Alberto.Dcb.Commands/ServiceCollectionExtensions.cs (2 sites).
         (Note: Task 12 deletes all pragmas anyway — likely moot.)
    M5.2 ServiceCollectionExtensions.cs `var final = declared;` is a redundant alias; use `declared` directly.
    M5.3 No test covers ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey).
    M5.4 Deferred_registrations_run_against_the_final_definition asserts only ModuleKey (set in ctor),
         so it does not actually prove the callback sees the post-lambda definition. Strengthen to assert
         TenancyEnabled when .Register() is declared before .WithTenancy().
Task 6: complete (commits 8173cc0..6e4b8af, review clean after one fix pass)
  - ControlLoopBuilder + ErrorPolicy deleted; ControlLoopOptions is now the single home for every knob.
  - ControlLoopRegistration.Register is deferred and reads IOptionsMonitor<AlbertoModuleDefinition>.Get(moduleKey)
    inside the factory lambdas, so configuration still wins at resolution time.
  - Implicit control loop RESTORED (ServiceCollectionExtensions.cs ~700), deferred, pinned by
    WithControlLoop_is_implied_when_it_is_never_called. Task 5's temporary removal is closed.
  - Reviewer blocker (Important): brief Step 7 call-site migration was skipped in OrdersModule.cs and
    PaymentsModule.cs -> NEW build errors naming the deleted ControlLoopBuilder. Fixed in 6e4b8af;
    verified only the pre-existing CS0407/Handles errors remain in those app projects.
  - Deviation accepted: new tests use a private StubBackend instead of .WithInMemory(), because the
    InMemory backend descriptor does not exist until Task 9. Reviewer judged the tests still real.
    ** TASK 9 ACTION: swap StubBackend for .WithInMemory() in ControlLoopConfigurationTests once the descriptor lands. **
  - Breaking changes for Task 13 UPGRADING.md:
    B6.1 ControlLoopBuilder -> WithControlLoop(Func<ControlLoopOptions, ControlLoopOptions>) with-expression.
    B6.2 ErrorPolicy -> RetryOptions + a separate IErrorClassifier argument.
    B6.3 IEventProcessor.HandleError(..., ErrorPolicy) removed (was dead; forced by ErrorPolicy deletion).
    B6.4 ConsumeMiddlewares/BatchConsumeMiddlewares.RetryAndDeadLetter signature is now
         (RetryOptions retry, IErrorClassifier classifier, IDeadLetterStore? deadLetterStore).
  - Deferred Minor findings:
    M6.1 AddConsumeMiddleware / AddBatchConsumeMiddleware / UseErrorClassifier (both overloads) are new
         public API with no direct tests.
Task 7: complete (commits 6e4b8af..38350e7, review clean — sonnet reviewer, Approved)
  - Every processor id flows through ProcessorId.For<THandler>() with `explicit ?? derived`; no second derivation.
  - ProcessorExecutionConfigurator deleted; execution is configured with `o => o with { ... }`.
  - ⚠️ item (does ALB0005 null-guard ProcessorDeclaration.Execution for projections?) — CLEARED by me:
    ProcessorDeclaration.Execution is non-nullable with `= ProcessorExecutionOptions.Default`, and
    AlbertoModuleValidator.cs:151 uses a null-safe `is { ... }` property pattern. No null risk.
  - Deviations accepted (all documented): test types moved to namespace level so ProcessorId.For returns the
    unqualified name; StubBackend again instead of .WithInMemory() (Task 9); duplicate-id test reads
    module.Definition inside the lambda because IOptionsMonitor.Get() would throw before the assertion.
  - Deferred Minor findings:
    M7.1 ProcessorRegistrationTests.cs:549 uses failures.Single(f => f.Code == "ALB0002"); if the validator
         ever emits one failure per extra occurrence this throws InvalidOperationException instead of a
         readable assertion failure. Prefer ContainSingle(...).Which.
    M7.2 No test covers the new DeclareProcessor call inside AddProjection<TState> (all 7 tests are reactor paths).
Task 8: complete (commits 38350e7..02d1176, review clean after one fix pass + one controller tweak)
  - Postgres is now a descriptor: WithPostgres only calls UseBackend; NO connection, migration, or service
    resolution happens during AddAlberto. Migrations run in AlbertoMigrationHostedService at host start.
  - Reviewer blocker (Important): the port DROPPED the MigrationResult check, so a failed DbUp migration let
    the host start with an incomplete schema. Fixed in 5b9121c with a new test that points at an unreachable
    connection string (no Docker needed) and asserts host.StartAsync throws naming the module.
  - Controller tweak: the fixer's `catch (Exception ex) when (ex is not InvalidOperationException)` was
    inverted in intent — it let a DbUp IOE through unlabelled while wrapping cancellation. Changed to
    `when (ex is not OperationCanceledException)`. Suite re-run green (646 passed / 4 skipped).
  - Implementer deviations, both verified sound by the reviewer:
    D8.1 Validation tests 5-6 call new AlbertoModuleValidator().Collect(definition) directly, because
         Resolve(services) throws OptionsValidationException before .Collect() can run.
    D8.2 Restored `singleTenant: !definition.TenancyEnabled` on PostgresMigrator.Migrate, which the brief's
         snippet had dropped. Dropping it would run multi-tenant scripts against single-tenant modules.
  - Docker WAS available; the Testcontainers Postgres tests ran against a real database.
  - Deferred Minor findings:
    M8.1 No test proves AddAlberto wires AlbertoModuleValidator as IValidateOptions — i.e. that an invalid
         Postgres module throws OptionsValidationException from Resolve(services). Tests 5-6 test the
         validator in isolation instead.
    M8.2 PRE-EXISTING, not introduced here: ICheckpointStore, IDeadLetterStore, ITenantProcessorLock,
         IProcessorLeaseManager and PostgresEventListener capture Options.Schema/LeaseDuration from the
         code-configured descriptor at registration time, so the configuration overlay reaches
         NpgsqlDataSource but NOT those. Worth a follow-up sub-project.
Task 9: complete (commits 02d1176..ffeeb8a, review clean — sonnet reviewer, Approved)
  - InMemoryBackendDescriptor added (SupportsTenancy => false); .WithInMemory() now only calls UseBackend.
  - EF registration deferred: WithEntityFramework wraps AddPooledDbContextFactory in builder.Register(...).
    AddEfProjection declares the processor OUTSIDE the callback (so the validator sees it) and registers inside.
  - All three remaining #pragma warning disable CS0618 in InMemory/EF are GONE.
  - CARRIED-FORWARD ACTION CLOSED: StubBackend deleted from ControlLoopConfigurationTests.cs (5 sites) and
    ProcessorRegistrationTests.cs (2 sites), replaced with .WithInMemory(). Reviewer verified assertions
    were preserved verbatim, not loosened.
  - ⚠️ item (does UseBackend record the descriptor and guard a double call?) — CLEARED by me: the Task 5
    reviewer verified the eager double-backend guard at DcbModuleBuilder.cs ~340, and
    A_second_backend_declaration_is_rejected_immediately pins it.
  - Deviation accepted: The_in_memory_backend_does_not_support_tenancy captures module.Definition inside the
    lambda instead of Resolve(services), same reason as D8.1 — validation throws first. Same meaning.
  - Deferred Minor findings:
    M9.1 AddEfProjection (both overloads) lacks ArgumentNullException.ThrowIfNull(builder) while every
         neighbouring method in the same files now has it. Pre-existing gap, newly visible inconsistency.
Task 10: complete (commits ffeeb8a..3d4e1cf, review clean after one fix pass)
  - WithTelemetry() now self-registers Alberto's OTel sources:
    AddOpenTelemetry().WithTracing(t => t.AddSource(AlbertoMetrics.Name)).WithMetrics(m => m.AddMeter(...)),
    all inside builder.Register(...). Additive and idempotent (SDK dedupes AddSource by name).
  - AddAlbertoInstrumentation (both overloads) marked [Obsolete]; call sites removed from
    apps/ServiceDefaults/Extensions.cs and the now-unused ProjectReference dropped from ServiceDefaults.csproj.
  - #pragma warning disable CS0618 removed from Alberto.Dcb.Telemetry.
  - InternalsVisibleTo("Alberto.Dcb.Telemetry") added to Alberto.Dcb.csproj — needed for `with` on
    AlbertoModuleDefinition's internal set properties; reviewer confirmed every other companion assembly
    was already on that list.
  - OpenTelemetry.Exporter.InMemory 1.15.3 added to Directory.Packages.props — the brief's Step 1 asks for it,
    it is referenced ONLY by the test project, and the version matches the other OTel pins.
  - Reviewer blockers (2 Important), both fixed in 3d4e1cf:
    F10.1 Telemetry:Enabled = false did NOT disable append-side telemetry — TelemetryAppendInterceptor and
          ITraceContextProvider were registered unconditionally while only the consume middleware checked the
          flag. Now both go through factory lambdas reading IOptionsMonitor at resolution, returning
          NoOpAppendInterceptor / NoOpTraceContextProvider when disabled. Test went red-then-green.
    F10.2 Two guarantees had no test. Added: two modules calling WithTelemetry() produce exactly one
          Alberto.Append activity; an app's own pre-existing AddOpenTelemetry().WithTracing(...) survives
          AddAlberto and both its source and Alberto's are collected. Both green without production change
          (expected — they pin existing guarantees).
  - Breaking changes for Task 13 UPGRADING.md:
    B10.1 AddAlbertoInstrumentation is [Obsolete]; WithTelemetry() registers the sources instead.
    B10.2 .WithTelemetry() now installs the OTel SDK unconditionally (TracerProvider/MeterProvider singletons).
          1.15.3 has no ConfigureOpenTelemetryTracerProvider, so a truly inert registration is not available.
          No exporters means no I/O, but it IS a behavioural difference worth an UPGRADING row.
  - Suite: 658 passed / 4 skipped / 0 failed.
Task 11: complete (commits 3d4e1cf..d2725f1, review Approved after one fix pass)
  - Orphan-checkpoint detection: OrphanCheckpointHostedService compares persisted checkpoint ids against
    definition.Processors.Select(p => p.ProcessorId). Policy Off performs no inventory read at all
    (FakeInventory.Calls == 0); Warn logs; Strict throws naming the orphan and the `ops checkpoint rename` hint.
  - New CLI subcommand `ops checkpoint rename` (net-new; nothing was half-renamed).
  - Multi-tenant isolation is by datasource/schema, so ListProcessorIdsAsync needs no tenant predicate.
  - Environment escalation (ServiceCollectionExtensions.cs ~233-243): outside Development the default Warn
    escalates to Strict, resolved lazily inside the .Configure<IServiceProvider> callback.
  - Reviewer blocker (1 Important), fixed in d2725f1:
    F11.1 The escalation block had zero test coverage — deleting it broke nothing. Added
          tests/Alberto.Dcb.Tests/Configuration/ProductionEscalationTests.cs. The two behaviour-pinning tests
          passed immediately (correctly reported as pinning, not red-green).
  - Controller-initiated fix folded into the same pass (was Minor M11.1, promoted): an operator who explicitly
    set OrphanPolicy = Warn in production config was silently escalated to Strict anyway, because Warn is also
    the default and the check compared values. Now the escalation is skipped when
    configuration.GetSection($"{declared.ConfigurationPath}:Checkpoints:OrphanPolicy").Exists() is true.
    Path reuses AlbertoModuleDefinition.ConfigurationPath. Test went genuinely red-then-green.
  - Breaking changes for Task 13 UPGRADING.md:
    B11.1 Outside Development, an unset OrphanPolicy now defaults to Strict, so a deployment whose handler was
          renamed at some point will FAIL AT STARTUP after upgrading instead of silently replaying from zero.
          Escape hatches: set OrphanPolicy explicitly in configuration (now honoured), or Off to disable.
  - Deferred Minor findings:
    M11.2 The orphan message's CLI hint shows only orphans[0] when several exist
          (OrphanCheckpointHostedService.cs:185).
    M11.3 Redundant explicit `using Xunit;` at OrphanCheckpointTests.cs:303.
    M11.4 A code-side explicit .Configure(d => d with { ... OrphanPolicy = Warn }) is STILL escalated — the
          builder has no "was this touched" marker and adding one would be a new knob. Configuration-side is
          honoured. Asymmetry must be called out in UPGRADING.md (Task 13).
  - Suite: 666 passed / 4 skipped / 0 failed.

SCOPE DECISION (controller, user AFK) — pre-existing apps/ build breakage is now IN scope.
  Merge-base 8e38d68 does not build: 9 errors in 3 files, all in apps/, all predating this branch
  (introduced by fc94a34 "migrate app projections to DeclareProjection", which converted Orders but left
  Payments on the deleted untyped API and half-converted Orders' return types).
    - apps/Alberto.Orders/.../Projections/OrderSummaryEfProjection.cs — 7x CS0407. The Apply methods return
      OrderSummaryEntity; On<TEvent> wants Func<TState,TEvent,ProjectionContext,ProjectionResult<TState>>.
      A method-group conversion will not apply the implicit TState -> ProjectionResult<TState> conversion.
    - apps/Alberto.Payments/.../Projections/{PaymentSummaryProjection,PaymentsOverviewProjection}.cs — 2x CS1061.
      They use .Handles<T>() / .DocumentId(...) / .Evolve(...), an untyped builder API that no longer exists;
      only the typed .On<TEvent>(id, apply) remains.
  Earlier default was "out of scope, CI (test project only) is the green gate". Reversed with data:
  Task 12 Steps 3, 5 and 6 all require apps/ to compile (migrate OrdersModule.cs, clean `dotnet build`,
  AppHost smoke test), and shipping a DX-focused 1.0 whose sample apps do not build defeats the purpose.
  Landing as its own preparatory commit before Task 12 so Task 12's diff stays reviewable.
apps/ repair: complete (commit 0768c25, preparatory — reviewed as part of Task 12's base)
  - OrderSummaryEfProjection: 7 Apply overloads' return type OrderSummaryEntity -> ProjectionResult<...>.
    A method-group conversion will not apply the implicit TState -> ProjectionResult<TState> conversion.
  - PaymentSummaryProjection / PaymentsOverviewProjection migrated from the deleted untyped
    .Handles/.DocumentId/.Evolve chain to typed .On<TEvent>(id, apply). The old `_ => state` default arm has
    no equivalent and needs none: unregistered event types are never dispatched in the typed API.
  - Also unblocked 4 files whose errors were masked by the Infrastructure failure:
    OrderMutations/PaymentMutations/OrderQueries/PaymentQueries — .Stream( -> .StreamAsync( (rename in
    IEventStoreBackend), and two PostgresStateStore call sites switched from positional to named args.
    THAT SECOND ONE WAS A REAL BUG, not just a compile fix: the call sites passed
    (dataSource, tenantId, projectionType, "orders") against (dataSource, projectionType, schema,
    rebuildVersion, tenantId), so tenantId landed in projectionType and projectionType in schema. Those
    GraphQL queries were reading the wrong projection from the wrong schema. Worth an UPGRADING/CHANGELOG note.
  - Build: 0 errors. 22 pre-existing NU1902 transitive-package vulnerability advisories remain (MessagePack
    2.5.192 via Aspire, others) — out of scope for this sub-project, flag to the user.
Task 12: complete (commits 0768c25..6b22197, opus review Approved, no Critical/Important)
  - DcbModuleBuilder.Services deleted along with the _services field and the DI using; ctor is now
    `internal DcbModuleBuilder(string moduleKey) => Definition = new AlbertoModuleDefinition { ModuleKey = moduleKey };`
  - Every CS0618 pragma labelled for the Task 5 bridge removed from DcbModuleBuilderExtensions.cs,
    MessagingBuilderExtensions.cs and Alberto.Dcb.Commands/ServiceCollectionExtensions.cs. All surviving
    CS0618 pragmas belong to the unrelated obsolete Projection<T> API and are correctly untouched.
  - The brief predicted ~50 broken test files; only ReactToScopedHandlerTests.cs changed. Reviewer verified
    this is benign — every other test already went through services.AddAlberto(...) from earlier tasks.
    Only setup changed in that file; no assertion weakened, deleted or made vacuous.
  - Deferred-registration rule verified clean: no builder.ModuleKey / builder.Definition access outside a
    Register(context => ...) callback. Shadowed `context` in the ReactorContext overload renamed to
    `reactorContext` (DcbModuleBuilderExtensions.cs:353,369,373-374).
  - OrdersModule.cs needed no change — already fluent, and 6e4b8af's .WithControlLoop survives at line 57.
  - Reviewer ⚠️ (controller-resolved, accepted): Step 6's AppHost smoke test was run with Docker and
    "Distributed application started" was observed, but the report does not quote the Orders API's
    "Applying Alberto migrations for module orders" line the brief asked for — child services start
    asynchronously past the 45s window. Accepted as a reporting gap, not a code defect: the migration path
    has direct red-then-green coverage from Task 8 (5b9121c/02d1176).
  - Deferred Minor:
    M12.1 ReactToScopedHandlerTests.cs:471,499,540 pass CancellationToken.None to ProcessEventAsync;
          the spec mandates TestContext.Current.CancellationToken. Pre-existing, not introduced here.
          Worth a repo-wide sweep in the final fix pass.
  - Build 0 errors. Suite: 666 passed / 4 skipped / 0 failed.
Task 13: complete (commits 6b22197..HEAD, docs only)
  - Created README.md (repo had never had one), UPGRADING.md 0.x->1.0 section, docs/configuration.md.
  - Every C# sample compiled in a scratch console project against Alberto.Dcb, .Postgres, .EntityFramework,
    .Telemetry, .InMemory — 0 errors, 0 warnings. Scratch project not committed.
  - Validation catalog verified against source: all 11 codes exist (ALB0001-ALB0007, ALB1001-ALB1004);
    none missing, none extra. ALB0004 covers four conditions (PollingInterval, HeadRefreshInterval,
    BatchSize, HeadWindowSize) and is documented as one row naming all four.
  - Implementer reported two things that were WRONG and I corrected:
    (a) "there is no CLI project in this repo" — false. tools/Alberto.Cli/ exists, IS in AlbertoV3.slnx:28,
        and `ops checkpoint rename` ships at Commands/Ops/CheckpointOpsCommand.cs:269. The hedging never
        reached the page (UPGRADING.md:32,47,53 reference the command plainly), so no doc fix was needed
        for this — but the report's claim is on record as inaccurate.
    (b) The M11.4 "code vs configuration asymmetry" I asked for in the supplement is UNREACHABLE and should
        never have been documented. There is no WithCheckpoints(...) builder method and
        AlbertoModuleDefinition.Checkpoints has `internal set`, so user code cannot set OrphanPolicy at all —
        configuration is the only path. Fixed by me: UPGRADING.md's "code vs. configuration" section replaced
        with an "Opting out of Strict" section carrying a real appsettings.json snippet, and
        docs/configuration.md:250-255 rewritten to state Checkpoints is configuration-only and why.
        M11.4 is hereby WITHDRAWN, not deferred — it describes a scenario that cannot occur.
  - Note: the escalation condition at ServiceCollectionExtensions.cs:73-74 tests
    `declared.Checkpoints.OrphanPolicy == Warn`, which is now known to be always true. Dead but harmless.
  - Build 0 errors. 126 warnings, ALL of them NU1902/NU1903 transitive-package advisories (my earlier count
    of 22 was an incremental-build artifact) — zero code warnings.

FOLLOW-UP FOR THE USER (not a defect, deliberately deferred):
  Checkpoints is the only options section without a With...() builder method, while Postgres, ControlLoop
  and Telemetry all have one. Adding WithCheckpoints(Func<CheckpointOptions,CheckpointOptions>) would make
  the API symmetric. Deferred because no task in the plan called for it and adding a builder method after
  1.0 is additive and non-breaking, so there is no cost to waiting. If it IS added later, the escalation
  logic must be revisited: a code-side explicit Warn would then be indistinguishable from the default.

## Minor sweep (final fix pass)

M5.4: resolved 9f2bd1b — test(config): strengthen deferred-registration test to assert TenancyEnabled is visible pre-registration
M6.1: resolved d1d1653 — test(config): add direct tests for AddConsumeMiddleware, AddBatchConsumeMiddleware and UseErrorClassifier
M7.2: resolved 01fddbe — test(config): add test verifying AddProjection<TState> declares a projection processor
M9.1: resolved 7fb5f23 — fix(ef): add ArgumentNullException.ThrowIfNull(builder) to both AddEfProjection overloads
M11.2: resolved 4430ff9 — fix(orphan): emit a rename command for every orphan, not only orphans[0]
M12.1: resolved 40c6f53 — test(subscriptions): replace CancellationToken.None with TestContext token in ReactToScopedHandlerTests
m1: resolved 126abac — refactor(config): remove dead declared.OrphanPolicy condition from escalation block
m2: resolved bd16844 — docs(config): correct misleading comments claiming configuration overlay has already been applied at Phase 3
m3: no change — AddAlbertoInstrumentation() is a supported path for manual TracerProvider wiring; a diagnostic would be a regression. See minor-sweep-report.md.

Build: 0 errors. Suite: 692 passed / 4 skipped / 0 failed.
