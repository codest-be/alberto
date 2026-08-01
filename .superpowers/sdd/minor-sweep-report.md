# Minor Sweep Report — Alberto 1.0 release-dx branch

**Build result**: 0 errors, 27 pre-existing NU1902/NU1903 / xUnit1051 warnings (all pre-existing, none introduced)  
**Test result**: Passed: 692 / Skipped: 4 / Failed: 0 (baseline was 687/4/0; 5 new tests added)

---

## Ledger findings

### M5.4 — 9f2bd1b
`tests/Alberto.Tests/Configuration/ModuleDefinitionTests.cs`

Strengthened `Deferred_registrations_run_against_the_final_definition`. Added capture of
`context.TenancyEnabled` in the callback, placed the `.Register(...)` call **before**
`.WithTenancy()` in the builder chain, and asserted `seenTenancyEnabled == true`. This
directly proves the callback sees the post-lambda definition, not a snapshot taken at the
point of registration.

---

### M6.1 — d1d1653
`tests/Alberto.Tests/Configuration/ConsumeMiddlewareRegistrationTests.cs` (new file)

Added four tests covering the new public API:
- `AddConsumeMiddleware_registers_a_keyed_ConsumeMiddleware_singleton`
- `AddBatchConsumeMiddleware_registers_a_keyed_BatchConsumeMiddleware_singleton`
- `UseErrorClassifier_instance_overload_registers_a_keyed_IErrorClassifier`
- `UseErrorClassifier_generic_overload_registers_a_keyed_IErrorClassifier`

Each test calls `BuildServiceProvider()` and resolves the keyed service to verify registration
was effective.

---

### M7.2 — 01fddbe
`tests/Alberto.Tests/Configuration/ProcessorRegistrationTests.cs`

Added `AddProjection_declares_a_projection_processor` test. Also added `[EventType]` to the
existing `ShipmentDispatched` record at namespace level (required by
`ProjectionDeclarationBuilder.On<TEvent>()` — reactor-only tests did not previously need it).
The test resolves the module definition and asserts `ProcessorId == "shipment-summary"` and
`Kind == ProcessorKind.Projection`.

---

### M9.1 — 7fb5f23
`src/Alberto.EntityFramework/EfConsumerBuilderExtensions.cs`

Added `ArgumentNullException.ThrowIfNull(builder)` as the first guard in both
`AddEfProjection` overloads, consistent with every neighbouring method in the same file.

---

### M11.2 — 4430ff9
`src/Alberto/OrphanCheckpointHostedService.cs`

When multiple checkpoints are orphaned the CLI hint previously named only `orphans[0]` in a
single `--from` clause. Replaced with a per-orphan list of rename commands joined by
`Environment.NewLine`, so the operator sees one actionable command line for each orphan.
Existing test only asserts `Contains("ops checkpoint rename")` — still satisfied.

---

### M12.1 — 40c6f53
`tests/Alberto.Tests/Subscriptions/ReactToScopedHandlerTests.cs`

Replaced all four `CancellationToken.None` arguments on `ProcessEventAsync` calls (actual
lines 72, 73, 103, 133 in the current file — ledger cited 471/499/540 from an earlier
snapshot) with `TestContext.Current.CancellationToken`.

---

## Unlisted findings

### m1 — 126abac
`src/Alberto/ServiceCollectionExtensions.cs`

**Dead condition**: `declared.Checkpoints.OrphanPolicy == OrphanCheckpointPolicy.Warn` (the
second `&&` clause in the escalation block) is always true. `AlbertoModuleDefinition.Checkpoints`
has `internal set` and there is no `WithCheckpoints()` builder method, so user code cannot
set `OrphanPolicy` away from its default of `Warn`. The clause can therefore never be false.

**Fix**: removed the dead clause and narrowed the comment to describe only the configuration
path, which the inner `orphanPolicySection?.Exists() != true` check still guards correctly.
No behaviour change.

---

### m2 — bd16844
`src/Alberto/ServiceCollectionExtensions.cs` and `src/Alberto/Configuration/AlbertoModuleContext.cs`

Both files claimed that configuration overlay had already been applied at Phase 3 / when the
`AlbertoModuleContext` is created. This is incorrect: the context is built from `declared` (the
code-only definition); overlay runs later inside the `.Configure<IServiceProvider>` Options
callback when `IOptionsMonitor` resolves at host startup.

**Fix**: updated the inline comment at Phase 3 and the `AlbertoModuleContext` XML doc comment
to accurately describe what is in the definition at that point.

---

### m3 — no change
**Decision**: no diagnostic warranted.

`AddAlbertoInstrumentation()` is the documented, supported path for wiring Alberto's OTel
sources into a manually-managed `TracerProvider` constructed outside DI. A preceding commit
on this branch deliberately un-obsoleted it for this reason. Any startup check of the form
"you called `AddAlbertoInstrumentation()` but not `.WithTelemetry()`" would fire on every
legitimate manual-wiring use case and constitute a regression against the un-obsoleting
commit. The scenario where a user intended `.WithTelemetry()` but forgot it and got silence
is a documentation concern (docs/configuration.md already covers both paths), not one that
can be addressed by a startup diagnostic without harming the supported path.
