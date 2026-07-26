# Wave 1 — test-suite remediation (SP0, SP1a, SP2, SP4)

Spec: `docs/superpowers/specs/2026-07-26-test-suite-remediation-design.md`
Plans: `docs/superpowers/plans/2026-07-26-sp{0,1a,2,4}-*.md`

## Base drift — READ THIS FIRST

All four branches were cut from `e3dd5ab`. That commit is **no longer an ancestor of
`origin/main`**: PRs #29 and #30 merged at 11:18 and 11:25 on 2026-07-26, after the
branches were cut. `origin/main` is now `c790b63` (which includes SP0's squash merge).

What landed on main that the branches do not have:

- Migrations `019_ValidateDeferredCheckConstraints`, `020_InFlightVisibilityIndex`,
  `021_CheckpointFenceTokens`, `022_TagConceptIndex`, `023_TagConceptBoundaryMatching`
  (both variants). **SP4's new migration must be renumbered 019 → 024.**
- `CachingCheckpointStore.cs`, `DeadLetterRetryLoopTests.cs` and
  `DeadLetterRetryLoopBehaviorTests.cs` all changed — SP2 touches all three.
- New `ProjectionCatchUp` + commit `6dae8cf` "wait for a projection instead of sleeping
  for it" — overlaps SP2's purpose; check for redundancy before finishing SP2.
- New test files: `AppendPrefixBoundaryTests`, `FencedCheckpointFenceTokenTests`,
  `MigrationTransactionModeTests`, `MigrationUpgradeAndParityTests` (moved/expanded),
  `ProjectionCatchUpTests`, `ProjectionCatchUpEndToEndTests`.

Every remaining branch must be rebased onto `origin/main` before its PR.

Baseline at the old base `e3dd5ab`: 1040 passed / 15 skipped / 0 failed.
Baseline at `origin/main` (`c790b63`): **1085 passed / 14 skipped** (derived: SP1a at 1093 with its 8 added tests; SP2 at 1086 with its 1).

All three branches rebased onto `c790b63`. SP1a green (1093), SP2 green (1086), SP4 red with exactly the 4 expected delete-on-flip failures (1083 passed).

Worktrees:

| Sub-project | Branch | Worktree | Tasks |
|---|---|---|---|
| SP0 | `sp0-coverage-in-ci` | merged, worktree removed | 1/1 done |
| SP2 | `sp2-timeprovider-seams` | `/Users/bjorn/dev/AlbertoV3-worktrees/sp2-timeprovider-seams` | 4 |
| SP4 | `sp4-rebuild-retention` | `/Users/bjorn/dev/AlbertoV3-worktrees/sp4-rebuild-retention` | 4 |
| SP1a | `sp1a-testing-packages` | `/Users/bjorn/dev/AlbertoV3-worktrees/sp1a-testing-packages` | 8 |

End condition (user): PR per sub-project → CI green → merge.

PRs:

- SP0 — https://github.com/codest-be/albertoo/pull/31 — **merged**, CI green, `coverage` artifact confirmed (122 KB)

## SP4 IS SUPERSEDED — STOP WORK ON IT

`origin/main` moved again to `1fd57c2` — PR #33, "Reclaim a discarded rebuild version
after a grace period, not inside the flip", authored by the repo owner at 11:40 on
2026-07-26. It implements SP4's entire goal by a different mechanism:

- No retirements table and no migration. The completion transitions simply stop deleting;
  `RebuildCoordinator`'s sweep reclaims the version via a new
  `IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync` once `ReclaimGracePeriod`
  (2x `ProjectionVersions.RefreshInterval`) has elapsed since the transition's `CompletedAt`.
- Both Known Gaps in CLAUDE.md are closed and the section now reads "None currently."
- The four tests SP4 Task 4 was going to update have already been updated on main.

SP4's branch (migration 024 + `alberto_projection_version_retirements` +
`ListRetiredVersionsAsync`) is a competing second mechanism for a problem main has already
solved. Task 4's implementer was stopped mid-run. **The branch is parked pending the
owner's decision** — it should almost certainly be abandoned, but that is not my call.

## Completed tasks

- SP0 Task 1: complete (commits e3dd5ab..b009d53, review clean — coverlet.collector + CI artifact, baseline line-rate 0.6666). MERGED.
- SP4 Task 1: complete (review clean — retention migration in both variants, byte-identical). Renumbered 019 → 024 on rebase (commit `chore(migrations): renumber the retention migration past main's 019-023`).
- SP4 Task 2: complete (review clean — retire instead of delete; branch deliberately red, 4 tests fail pending Task 4)
- SP2 Task 1: complete (commits e3dd5ab..dfca14b, review clean — TimeProvider seam on CachingCheckpointStore, both Task.Delay sleeps gone, 1041 passed)
- SP2 Task 2: complete (review Approved with one Important finding, fix dispatched — TimeProvider seam on DeadLetterRetryLoop)
- SP1a Task 1: complete (commits e3dd5ab..f3a1b59, review clean — Alberto.Dcb.Testing package + Poll.UntilAsync, no xunit dependency, 1044 passed)
- SP1a Task 2: complete (review clean — TestEvents.NewEvent)
- SP1a Task 3: complete (review clean — EventCollector promoted into the package, TimeProvider-driven, 1096 passed)
- SP1a Task 4: complete (commit d2022b0, review clean — InMemoryOutboxStore, faithful claim-lease semantics vs Postgres, 1099 passed)
- SP2 Task 3: complete (commits e062b15, cd5eca2, review clean — deterministic dead-letter timestamps; both brief deviations judged forced and correct, 1087 passed)
- SP4 Task 3: complete (commits 7e4d5cf + fix b758105, re-review Approved — retirement-aware sweep; the dead `stillRetained` guard replaced by a real `ListRetiredVersionsAsync`, plus a guard test verified to fail without it)
- SP2 Task 4: complete (commits cb9d19e + fix 256faf4, re-review Approved — remaining wall-clock waits documented or removed; `PollSignalingOutboxStore` replaces the OutboxRelay sleep with a structural sync point). **SP2 is feature-complete: full suite 1087 passed / 0 failed / 14 skipped.**
- SP2 Task 2 fix: re-review Approved — `PollSignalingDeadLetterStore.WhenPolled` gives a structural (not timing) synchronization point; `Assert.Empty` intact

- SP1a Task 5: complete (commits d2022b0..021daa1, spec ✅ / quality Approved). `AlbertoTestHarness` boots a Generic Host, appends, and waits for control-loop quiescence. Two implementer deviations judged sound by the reviewer: quiescence reads `IOptionsMonitor<AlbertoModuleDefinition>` (declared processors) instead of `ICheckpointInventory` (which is vacuously empty before the first checkpoint), and `TestEvents` drops `JsonSerializerDefaults.Web` because camelCase does not round-trip through `ParseEvent<T>`. Stalled-path test verified non-vacuous. Suite 1101 passed / 0 failed / 14 skipped.

- SP1a Task 6: complete (commits 021daa1..2ed987c, spec ✅ / quality Approved after one fix). Adds `Alberto.Dcb.Testing.Xunit` with five conformance specifications (state store, dead letter, outbox + the two promoted ones) and four derivations. Reviewer caught an Important defect — `ClaimPendingAsync_HeldLease_IsNotReclaimable` was gated on `FakeTimeProvider` despite never touching a clock, so the Postgres derivation skipped the one fact proving `FOR UPDATE SKIP LOCKED`-style exclusion; a backend with no concurrency exclusion would have passed the whole suite. Fixed in `2ed987c`; Postgres now runs it. Suite 1161 passed / 0 failed / 17 skipped.

- SP1a Task 7: complete (commit d7ec7e6, spec ✅ / quality Approved, no fixes needed). One canonical `FakeBackend` replaces three divergent nested copies; `Testing/Events.cs` gives the shared event vocabulary. Reviewer verified both reported semantic changes are inert (`bool?` + FluentAssertions `BeTrue` is if anything stricter; `UnknownConfigurationKeyTests` asserts only on ALB0008 codes and never branches on `SupportsTenancy`) and confirmed the eight remaining duplicate-event files are explicitly SP1b scope, not under-delivery. Suite 1162 passed / 0 failed / 17 skipped.

- SP1a Task 8: complete (commit 23da001, spec ✅ / quality Approved, no findings). Both packages ship. Reviewer independently ran `dotnet pack` and read the extracted nuspecs rather than trusting the implementer's grep: both target frameworks carry dependency groups in both packages, all metadata (MIT, authors, repo url, `VersionPrefix`, `IncludeSymbols`/snupkg) flows from `Directory.Build.props` identically to the pre-existing packable projects, `Alberto.Dcb.Testing`'s dependency groups contain no test framework, and the release workflow's `artifacts/Alberto.Dcb*.nupkg` glob already matches both new ids. No unintended public types leak. **SP1a is feature-complete: 1162 passed / 0 failed / 17 skipped.**

## Standing user instruction (received during Task 7)

> "when done, verify all skipped tests and see if the reason is valid or if we should fix the test/code"

Before opening the SP1a PR: enumerate every skipped test in the suite (17 at Task 7), establish why each one skips, and judge per test whether the skip is legitimate (e.g. a capability the backend genuinely lacks) or is masking a defect in the test or the production code. Report the verdicts to the user.

## Minor findings carried to the final review

- SP2 Task 1 — `CachingCheckpointStoreTests.cs:148` `WaitForInnerAsync` is called with `cache`, not `inner`, in the resync test; the name misleads. Rename to something like `WaitForValueAsync`.
- SP2 Task 1 — `CachingCheckpointStoreTests.cs:148-155` the yield loop returns silently on exhaustion, so a genuine hang surfaces as a value-mismatch assertion rather than a timeout. Plan-mandated shape; a `throw` on exhaustion would be more diagnosable.
- SP1a Task 1 — `PollTests.cs` exercises `Poll.UntilAsync` only against `TimeProvider.System`; no test drives it with a fake clock, so the `TimeProvider` wiring (deadline arithmetic + `Task.Delay`) is correct by construction but unverified. Add a `FakeTimeProvider` test.
- SP1a Task 2 — `TestEvents.cs` doc comments use `<c>EventToPersist</c>` / `<c>EventTypeAttribute</c>` where `<see cref="..."/>` would resolve (both types live in `Alberto.Dcb`, which the package references). Loses IDE navigation on a shipped surface.
- SP1a Task 2 — no test covers `metadata` pass-through; every other parameter has one.

- SP2 Task 3 — `ControlLoopAssembler` never passes a `TimeProvider` to the middleware factories, so the provider injected into `DeadLetterRetryLoop` does not reach the middleware `FailedAt` stamps in production; the seam is test-only. The brief's own approach had the same hole. Decide at the final review whether the assembler should thread it through.
- SP4 Task 3 — `RetainedVersionsAsync` calls `ClearAsync` for collected versions, and `SweepAsync` then calls it again for the same versions. Idempotent and harmless, but the inner call is strictly redundant and could be removed.
- SP4 — `Rebuild_CatchesUpOnEventsThatArriveWhileItIsRunning` is flaky under concurrent load (passes in isolation). Unrelated to SP4's diff, but SP4 exists to kill rebuild flakiness — worth a look before merge.
- SP2 Task 4 — `ProjectionCatchUpTests.cs` sleep comment explains what the sleep guards but not why no structural sync point is possible; `GrowingHeadBackend` already tracks `HeadReads` via `Interlocked`, so a TCS firing at the first read would remove the sleep.
- SP2 Task 4 — `ControlLoopAssemblerTests.cs:135,204` and `AppendPrefixBoundaryTests.cs:235` are inline polling loops left uncommented; classed as SP1b scope.
- SP2 Task 3 — the new test builds `FailedAt: DateTimeOffset.UtcNow` (real wall clock) for a field it does not assert; `time.GetUtcNow()` would be cleaner.

- SP1a Task 5 — reviewer flagged `public string ModuleKey` as extra API surface beyond the brief (YAGNI on a shipped package). **The premise is wrong**: the brief specifies `public string ModuleKey => _moduleKey;` verbatim at line 156. Plan-mandated, so no fix dispatched. The underlying concern — an unused public property is a permanent compatibility commitment — still stands for the final review to triage.
- SP1a Task 5 — `AlbertoTestHarness.AppendAsync`'s `tenantId` flows into `TenantContext.SetTenant`, which rejects UUIDs and hyphenated ids; the XML docs do not say so. It also silently ignores a non-null `tenantId` when tenancy is not registered.

- SP1a Task 6 — `RetryFailedAsync_ResetsFailed_ToPending` uses `Assert.Contains` rather than `Assert.Single` because `RetryFailedAsync()` is global (unscoped by processor) and the shared Postgres fixture leaks `Failed` entries between facts. Correct behaviour is still asserted, but the weaker assertion would not catch an entry-duplicating bug. A per-fact fixture scope would let it be `Assert.Single`.
- SP1a Task 6 — the csproj takes `xunit.v3.extensibility.core` + `xunit.v3.assert` rather than the `xunit.v3` meta-package the brief's template used, because the meta-package requires `<OutputType>Exe</OutputType>` and cannot build as a library. Correct, but worth confirming the package's declared dependencies at Task 8 packaging time.

## Open fixes (dispatched or pending)

- SP4 Task 2 — `CollectRetiredVersionsAsync` deletes from the states table filtering only on `projection_type` and `rebuild_version`, not `processor_id`. Pre-existing (inherited from `DeleteStateVersionAsync`), but now that deletion is decoupled from the flip and can land much later, two processors sharing a `projection_type` could have the wrong rows swept. Consider adding a `processor_id` filter if the schema permits.
- SP4 Task 2 — `RetiredVersion` carries `<param>` docs for all four members where the brief specified only `RetiredAt`. Unrequested but correct.
- SP1a Task 3 — `EventCollector` owns a `SemaphoreSlim` but does not implement `IDisposable`. Non-breaking to add later, but it is a shipped public type.
- SP1a Task 3 — `EventCollector`'s class doc says "Wire to `PollingConsumer.OnProjected` to use"; `PollingConsumer` does not exist until Task 5.
- SP2 Task 2 fix — `PollSignalingDeadLetterStore.WhenPolled` doc says "when the call returns"; the signal actually fires before the return statement. Harmless now, misleading if the inner store ever becomes genuinely async.
