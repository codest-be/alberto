# SP2: TimeProvider seams and wall-clock removal — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push the `TimeProvider` seam past the storage backends into the control-loop types that schedule work, so tests drive time instead of sleeping through it.

**Architecture:** Each target type gains an optional trailing `TimeProvider? timeProvider = null` constructor parameter defaulting to `TimeProvider.System`, so every existing call site keeps compiling. Raw `Timer` becomes `ITimer` from `TimeProvider.CreateTimer`; `Task.Delay(x, ct)` becomes `Task.Delay(x, _timeProvider, ct)`; direct `DateTimeOffset.UtcNow` becomes `_timeProvider.GetUtcNow()`. Tests then inject `FakeTimeProvider` and call `Advance`.

**Tech Stack:** .NET 10.0, `Microsoft.Extensions.TimeProvider.Testing` 10.5.0 (already referenced by the test project), xUnit v3 3.2.2.

## Global Constraints

- Target framework is `net10.0`. Do not add a second TFM.
- NuGet versions are centrally managed in `Directory.Packages.props`. A `PackageReference` carries **no** `Version` attribute.
- **Every new constructor parameter is optional and trailing.** This sub-project must not break a single call site. If a change forces an existing caller to be edited, the design is wrong — stop and reconsider.
- Tests use `TestContext.Current.CancellationToken`, not `CancellationToken.None` — follow the surrounding convention in the file being edited.
- The suite must stay green. Run `dotnet test` before every commit and never commit red.
- PostgreSQL-backed tests use Testcontainers and require a running Docker daemon locally.
- Branch for this sub-project: `sp2-timeprovider-seams`, off `main`.

### Scope boundary against SP1a and SP1b

SP2 removes `Task.Delay` calls **that a `TimeProvider` seam makes unnecessary**. It does **not** unify the three divergent polling helpers (`WaitForAsync` in `ControlLoopTests.cs:996` and `ControlLoopMiddlewareTests.cs:~285`, `WaitUntilAsync` in `ProjectionRebuildEndToEndTests.cs:458`). That is SP1b's job, once SP1a has shipped a canonical one. Leave those helpers alone — touching them here guarantees a merge conflict in Wave 2.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs` | Batched checkpoint flushing on two timers | Modify — timers become `ITimer` |
| `src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs` | Poll loop and dead-letter timestamp | Modify — delays and `UtcNow` |
| `src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs` | Single-event failure timestamp | Modify — `UtcNow` |
| `src/Alberto.Dcb/Subscriptions/BatchConsumeMiddleware.cs` | Batch failure timestamp | Modify — `UtcNow` |
| `src/Alberto.Dcb.InMemory/InMemoryDeadLetterStore.cs` | In-memory dead-letter storage | Modify — `UtcNow` |
| `tests/.../Subscriptions/CachingCheckpointStoreTests.cs` | Drives flush/resync deterministically | Modify — delete two 200 ms sleeps |
| `tests/.../Subscriptions/DeadLetterRetryLoopBehaviorTests.cs` | Drives the retry poll deterministically | Modify — delete one 250 ms sleep |

`CachingCheckpointStore` and `DeadLetterRetryLoop` are the two that carry real scheduling. The rest are timestamp-only changes that exist so failure records can be asserted exactly rather than approximately.

---

### Task 1: TimeProvider on CachingCheckpointStore

`CachingCheckpointStore` builds two raw `Timer`s in its constructor with no seam at all, which is why `CachingCheckpointStoreTests` sleeps 200 ms twice to let a 100 ms-ish interval fire.

**Files:**
- Modify: `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs:32-33, 52-63, 314-315`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/CachingCheckpointStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CachingCheckpointStore(ICheckpointStore inner, TimeSpan? flushInterval = null, TimeSpan? resyncInterval = null, ILogger<CachingCheckpointStore>? logger = null, TimeProvider? timeProvider = null)`. The type is `internal sealed`; the test project reaches it through the existing `InternalsVisibleTo`.

- [ ] **Step 1: Create the branch**

```bash
git switch -c sp2-timeprovider-seams
```

- [ ] **Step 2: Write the failing test**

Add to `tests/Alberto.Dcb.Tests/Subscriptions/CachingCheckpointStoreTests.cs`. Match the file's existing `using` block; it will need `Microsoft.Extensions.Time.Testing`.

```csharp
[Fact]
public async Task Flush_WhenIntervalElapses_WritesThroughToInner_WithoutSleeping()
{
    var inner = new InMemoryCheckpointStore();
    var time = new FakeTimeProvider();
    await using var store = new CachingCheckpointStore(
        inner,
        flushInterval: TimeSpan.FromSeconds(30),
        resyncInterval: TimeSpan.FromMinutes(5),
        timeProvider: time);

    await store.SaveAsync("proc-1", 42, TestContext.Current.CancellationToken);

    // Nothing has reached the inner store yet: the write is only cached.
    Assert.Null(await inner.GetAsync("proc-1", TestContext.Current.CancellationToken));

    time.Advance(TimeSpan.FromSeconds(30));

    // The timer callback is async void, so yield until it has run rather than
    // asserting immediately. This is a scheduling yield, not a wall-clock wait.
    await WaitForInnerAsync(inner, "proc-1", 42);

    Assert.Equal(42, await inner.GetAsync("proc-1", TestContext.Current.CancellationToken));
}

private static async Task WaitForInnerAsync(ICheckpointStore inner, string processorId, long expected)
{
    for (var i = 0; i < 100; i++)
    {
        if (await inner.GetAsync(processorId, TestContext.Current.CancellationToken) == expected) return;
        await Task.Yield();
    }
}
```

- [ ] **Step 3: Run the test and verify it fails to compile**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~Flush_WhenIntervalElapses"
```

Expected: a build error, `CS1739: The best overload for 'CachingCheckpointStore' does not have a parameter named 'timeProvider'`.

This is the correct failure. If it instead fails at runtime, the parameter already exists and this task is already done.

- [ ] **Step 4: Add the seam**

In `src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs`, change the two timer fields at lines 32-33 from:

```csharp
    private readonly Timer _flushTimer;
    private readonly Timer _resyncTimer;
```

to:

```csharp
    private readonly ITimer _flushTimer;
    private readonly ITimer _resyncTimer;
```

Then replace the constructor (lines 52-63) with:

```csharp
    public CachingCheckpointStore(
        ICheckpointStore inner,
        TimeSpan? flushInterval = null,
        TimeSpan? resyncInterval = null,
        ILogger<CachingCheckpointStore>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(30);
        _resyncInterval = resyncInterval ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        var time = timeProvider ?? TimeProvider.System;
        _flushTimer = time.CreateTimer(OnFlushTimer, null, _flushInterval, _flushInterval);
        _resyncTimer = time.CreateTimer(OnResyncTimer, null, _resyncInterval, _resyncInterval);
    }
```

`ITimer` implements both `IDisposable` and `IAsyncDisposable`, so the `await _flushTimer.DisposeAsync()` calls at lines 314-315 need no change.

Add the XML doc line for the new parameter above the constructor, alongside the existing ones:

```csharp
    /// <param name="timeProvider">Clock used to schedule flush and resync. Defaults to <see cref="TimeProvider.System"/>.</param>
```

- [ ] **Step 5: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~Flush_WhenIntervalElapses"
```

Expected: PASS, 1 test.

- [ ] **Step 6: Delete the two wall-clock sleeps**

In `tests/Alberto.Dcb.Tests/Subscriptions/CachingCheckpointStoreTests.cs`, find the two tests containing `await Task.Delay(200` (originally lines 262 and 399). For each:

1. Change the store construction to pass `timeProvider: time` with a `FakeTimeProvider time = new()` declared above it.
2. Replace `await Task.Delay(200, ...);` with `time.Advance(<the interval that test configured>);` followed by the `WaitForInnerAsync` helper from Step 2 if the assertion reads through to the inner store.

Do not change what either test asserts. If an assertion has to move or weaken to make this work, stop — that means the sleep was hiding a real behavioural question, and it belongs in a separate commit with its own reasoning.

- [ ] **Step 7: Verify no sleeps remain in the file**

Run:

```bash
grep -n "Task.Delay" tests/Alberto.Dcb.Tests/Subscriptions/CachingCheckpointStoreTests.cs
```

Expected: no output.

- [ ] **Step 8: Run the full suite and commit**

Run:

```bash
dotnet test
```

Expected: `Passed: 910, Skipped: 2` — the original 909 plus the new test.

```bash
git add src/Alberto.Dcb/Subscriptions/CachingCheckpointStore.cs tests/Alberto.Dcb.Tests/Subscriptions/CachingCheckpointStoreTests.cs
git commit -m "feat: TimeProvider seam on CachingCheckpointStore

The flush and resync timers were raw System.Threading.Timers built in
the constructor, so tests had to sleep 200ms twice to let them fire.
They are now ITimers from an injected TimeProvider, defaulting to
TimeProvider.System so no call site changes."
```

---

### Task 2: TimeProvider on DeadLetterRetryLoop

**Files:**
- Modify: `src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs:15-24, 201, 210`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs:504`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `DeadLetterRetryLoop(IEventProcessor processor, IDeadLetterStore deadLetterStore, TimeSpan? pollingInterval = null, int batchSize = 10, IReadOnlyList<ConsumeMiddleware>? middlewares = null, ILogger<DeadLetterRetryLoop>? logger = null, TimeSpan? claimLeaseDuration = null, string? claimedBy = null, IServiceScopeFactory? scopeFactory = null, TimeProvider? timeProvider = null)`. Note `timeProvider` goes **last**, after `scopeFactory`.

- [ ] **Step 1: Add the seam**

`DeadLetterRetryLoop` uses a primary constructor. Add the parameter at the end of the list at line 15-24:

```csharp
public sealed class DeadLetterRetryLoop(
    IEventProcessor processor,
    IDeadLetterStore deadLetterStore,
    TimeSpan? pollingInterval = null,
    int batchSize = 10,
    IReadOnlyList<ConsumeMiddleware>? middlewares = null,
    ILogger<DeadLetterRetryLoop>? logger = null,
    TimeSpan? claimLeaseDuration = null,
    string? claimedBy = null,
    IServiceScopeFactory? scopeFactory = null,
    TimeProvider? timeProvider = null) : IHostedService, IAsyncDisposable
```

Add the backing field next to the other `private readonly` fields:

```csharp
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

- [ ] **Step 2: Route both delays through the provider**

At line 201, change:

```csharp
                    await Task.Delay(_pollingInterval, ct);
```

to:

```csharp
                    await Task.Delay(_pollingInterval, _timeProvider, ct);
```

At line 210, apply the identical change to the delay in the `catch (Exception ex)` block.

`Task.Delay(TimeSpan, TimeProvider, CancellationToken)` is a framework overload; no helper is needed.

- [ ] **Step 3: Build to confirm nothing broke**

Run:

```bash
dotnet build tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj
```

Expected: `Build succeeded`, zero errors. Because the parameter is optional and trailing, no existing caller needed editing — if the build reports otherwise, revert and reconsider.

- [ ] **Step 4: Replace the sleep in the behaviour test**

In `tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs`, find the test containing `await Task.Delay(250` at line 504. Construct its loop with `timeProvider: time` from a `FakeTimeProvider time = new()`, then replace the sleep with:

```csharp
        time.Advance(pollingInterval);
```

using whatever local the test already passes as `pollingInterval`. If the test currently passes the interval inline, hoist it to a local first so both the constructor and the `Advance` call use one value — an `Advance` that does not match the configured interval is a silent no-op and the test would hang rather than fail clearly.

- [ ] **Step 5: Run the affected tests**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~DeadLetterRetryLoop"
```

Expected: PASS, with no test taking longer than ~50 ms.

- [ ] **Step 6: Run the full suite and commit**

Run:

```bash
dotnet test
```

Expected: `Passed: 910, Skipped: 2`.

```bash
git add src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs
git commit -m "feat: TimeProvider seam on DeadLetterRetryLoop

Both poll delays now run through an injected TimeProvider, so the
behaviour test advances a fake clock instead of sleeping 250ms."
```

---

### Task 3: Deterministic dead-letter timestamps

Four places stamp a failure time from the ambient clock, so no test can assert the recorded time — only that it is roughly now.

**Files:**
- Modify: `src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs:55`
- Modify: `src/Alberto.Dcb/Subscriptions/BatchConsumeMiddleware.cs:72`
- Modify: `src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs:310`
- Modify: `src/Alberto.Dcb.InMemory/InMemoryDeadLetterStore.cs`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs`

**Interfaces:**
- Consumes: the `_timeProvider` field added to `DeadLetterRetryLoop` in Task 2.
- Produces: `ConsumeMiddleware` and `BatchConsumeMiddleware` each gain a `protected TimeProvider TimeProvider { get; init; } = TimeProvider.System;` property. `InMemoryDeadLetterStore` gains `InMemoryDeadLetterStore(TimeProvider? timeProvider = null)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs`:

```csharp
[Fact]
public async Task DeadLetterEntry_StampsCreatedAt_FromTheInjectedClock()
{
    var time = new FakeTimeProvider();
    time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    var store = new InMemoryDeadLetterStore(time);

    await store.StoreAsync(
        new DeadLetterEntry
        {
            ProcessorId = "proc-1",
            EventId = Guid.NewGuid(),
            EventType = "order-created",
            Position = 1,
            Error = "boom",
        },
        TestContext.Current.CancellationToken);

    var entries = await store.GetAsync("proc-1", 10, TestContext.Current.CancellationToken);

    Assert.Equal(
        new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Assert.Single(entries).CreatedAt);
}
```

Before running it, open `src/Alberto.Dcb/Subscriptions/DeadLetterEntry.cs` and match the required members and the exact type of `CreatedAt` — the assertion above assumes `DateTime` in UTC, matching the `DateTime.UtcNow` currently at `DeadLetterRetryLoop.cs:310`. If it is `DateTimeOffset`, compare against `time.GetUtcNow()` instead.

- [ ] **Step 2: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~StampsCreatedAt_FromTheInjectedClock"
```

Expected: a build error, `CS1729: 'InMemoryDeadLetterStore' does not contain a constructor that takes 1 arguments`.

- [ ] **Step 3: Add the seam to InMemoryDeadLetterStore**

In `src/Alberto.Dcb.InMemory/InMemoryDeadLetterStore.cs`, give the class a primary constructor parameter and a backing field:

```csharp
public sealed class InMemoryDeadLetterStore(TimeProvider? timeProvider = null) : IDeadLetterStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
```

Then replace every `DateTime.UtcNow` / `DateTimeOffset.UtcNow` in the file with `_timeProvider.GetUtcNow().UtcDateTime` / `_timeProvider.GetUtcNow()` respectively, matching each site's existing type.

- [ ] **Step 4: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~StampsCreatedAt_FromTheInjectedClock"
```

Expected: PASS, 1 test.

- [ ] **Step 5: Commit the in-memory store**

```bash
dotnet test
```

Expected: `Passed: 911, Skipped: 2`.

```bash
git add src/Alberto.Dcb.InMemory/InMemoryDeadLetterStore.cs tests/Alberto.Dcb.Tests/Subscriptions/DeadLetterRetryLoopBehaviorTests.cs
git commit -m "feat: TimeProvider seam on InMemoryDeadLetterStore"
```

- [ ] **Step 6: Seam the two middleware failure stamps**

In `src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs`, add to the class body:

```csharp
    /// <summary>Clock used to stamp failure records. Defaults to <see cref="TimeProvider.System"/>.</summary>
    protected TimeProvider TimeProvider { get; init; } = TimeProvider.System;
```

and change line 55 from `FailedAt: DateTimeOffset.UtcNow,` to `FailedAt: TimeProvider.GetUtcNow(),`.

Apply the identical change to `src/Alberto.Dcb/Subscriptions/BatchConsumeMiddleware.cs` at line 72.

An `init`-only property rather than a constructor parameter, because both types are inherited by middleware in tests and in `src`, and adding a constructor parameter to a base class does force every derived type to be edited — which Global Constraints forbid.

- [ ] **Step 7: Seam the retry loop's own stamp**

In `src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs` line 310, change:

```csharp
            CreatedAt = entry.CreatedAt ?? DateTime.UtcNow,
```

to:

```csharp
            CreatedAt = entry.CreatedAt ?? _timeProvider.GetUtcNow().UtcDateTime,
```

using the field added in Task 2.

- [ ] **Step 8: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 911, Skipped: 2`.

```bash
git add src/Alberto.Dcb/Subscriptions/ConsumeMiddleware.cs src/Alberto.Dcb/Subscriptions/BatchConsumeMiddleware.cs src/Alberto.Dcb/Subscriptions/DeadLetterRetryLoop.cs
git commit -m "feat: TimeProvider seam on dead-letter failure timestamps

ConsumeMiddleware and BatchConsumeMiddleware take the clock as an
init-only property rather than a constructor parameter, so derived
middleware in src and tests keep compiling unchanged."
```

---

### Task 4: Audit the remaining sleeps and record what stays

Eleven `Task.Delay` sites remain after Tasks 1-3. Some are seamable, some are not, and the difference needs to be written down rather than rediscovered.

**Files:**
- Modify: `src/Alberto.Dcb.Postgres/PostgresProcessorLeaseManager.cs`
- Modify: `src/Alberto.Dcb.Postgres/PostgresTenantProcessorLock.cs`
- Test: `tests/Alberto.Dcb.Tests/Subscriptions/ProcessorLeaseManagerTests.cs:105, 133`

**Interfaces:**
- Consumes: nothing.
- Produces: no new public surface if the check in Step 2 shows lease expiry is server-side.

- [ ] **Step 1: Enumerate what is left**

Run:

```bash
grep -rn "Task.Delay" tests/Alberto.Dcb.Tests --include="*.cs"
```

Expected: roughly eleven lines. Compare against this inventory from the audit — anything not on it is new and needs explaining:

```
PostgresDeadLetterStoreTests.cs:72                Task.Delay(50)
Subscriptions/ControlLoopTests.cs:649,680,790     Task.Delay(50/10/50)
Subscriptions/ControlLoopTests.cs:1002            Task.Delay(15)   // inside WaitForAsync
Subscriptions/EventStoreHeadBarrierTests.cs:73    Task.Delay(20)
Subscriptions/ProcessorLeaseManagerTests.cs:105   Task.Delay(250)
Subscriptions/ProcessorLeaseManagerTests.cs:133   Task.Delay(50)
Subscriptions/ProjectionRebuildEndToEndTests.cs:467  Task.Delay(50)  // inside WaitUntilAsync
Messaging/OutboxRelayTests.cs:151                 Task.Delay(50)
Subscriptions/TenantProcessorLockTests.cs:43,159,184  Task.Delay(10/150/50)
Subscriptions/ControlLoopMiddlewareTests.cs:289   Task.Delay(15)   // inside WaitForAsync
Postgres/PostgresEventListenerTests.cs:138        Task.Delay(300)
```

The three marked "inside `WaitForAsync`/`WaitUntilAsync`" are poll intervals inside the helpers SP1b will unify. **Leave them.** See the scope boundary above.

- [ ] **Step 2: Determine whether lease expiry is client-side or server-side**

Run:

```bash
grep -n "UtcNow\|now()\|expires_at\|lease_expires" src/Alberto.Dcb.Postgres/PostgresProcessorLeaseManager.cs src/Alberto.Dcb.Postgres/PostgresTenantProcessorLock.cs
```

This decides the task:

- **If expiry is computed in SQL with `now()`** — a client-side `TimeProvider` cannot move it, so `ProcessorLeaseManagerTests.cs:105` and `:133` and the three `TenantProcessorLockTests` sleeps must stay. Go to Step 3.
- **If expiry is computed in C# from `DateTime.UtcNow` and passed as a parameter** — add a trailing `TimeProvider? timeProvider = null` to both types exactly as in Task 2, replace the `UtcNow` reads, and convert those five sleeps to `time.Advance(...)`. Then go to Step 4.

Both outcomes are legitimate. Do not force a seam onto a server-side clock — a `TimeProvider` that does not actually control the value under test is worse than an honest sleep, because it reads as determinism that is not there.

- [ ] **Step 3: Document the sleeps that stay**

For each remaining sleep that is **not** inside a polling helper, add a one-line comment above it stating why it cannot be seamed. For a server-side lease expiry:

```csharp
        // Wall-clock, deliberately: the lease expiry is evaluated by PostgreSQL's now(),
        // which no client-side TimeProvider can move. Advancing a fake clock here would
        // read as determinism that is not there.
        await Task.Delay(250, TestContext.Current.CancellationToken);
```

`PostgresEventListenerTests.cs:138` is in this category too — it waits on a `NOTIFY` round-trip through the database.

- [ ] **Step 4: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 911, Skipped: 2` if Step 2 took the server-side branch; the same count with a faster `ProcessorLeaseManagerTests` if it took the client-side branch.

```bash
git add -A
git commit -m "test: document or remove the remaining wall-clock waits

Every Task.Delay left in the suite is now either a poll interval inside
a helper SP1b will unify, or carries a comment saying which clock it is
actually waiting on and why a TimeProvider cannot move it."
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin sp2-timeprovider-seams
```

Open a PR titled `SP2: TimeProvider seams and wall-clock removal`. In the description, list which sleeps were removed and which were documented as staying, so the reviewer checks the judgement rather than the diff.

---

## Self-Review

**Spec coverage.** The spec's SP2 entry asks for `TimeProvider` seams and deletion of the wall-clock waits, naming `CachingCheckpointStore` as having no seam at all and listing nine `src` files calling `DateTime(Offset).UtcNow` directly. Tasks 1-3 cover `CachingCheckpointStore`, `DeadLetterRetryLoop`, `ConsumeMiddleware`, `BatchConsumeMiddleware` and `InMemoryDeadLetterStore` — five of those nine. Task 4 covers the two Postgres lease types conditionally.

Four of the nine named files are **not** covered here: `Messaging/OutboxHandler.cs`, `EntityFramework/EfStateStore.cs` and `EntityFramework/Inline/DeclaredEfInlineProjection.cs`. None of them has a test that sleeps, so seaming them buys nothing this sub-project is for, and doing it anyway would widen a Wave 1 branch that has to stay disjoint from SP4. They are listed here so their absence is a decision rather than an oversight; SP6's sweep is the right home if they are ever wanted.

**Placeholder scan.** Task 1 Step 6 and Task 4 Step 2 both branch on what the implementer finds in the file, rather than showing one fixed edit. Both give the exact command to run and the exact decision rule, and Task 4 Step 3 shows the full comment text for the branch that produces one. This is a genuine fork in the work, not an unwritten step.

**Type consistency.** `timeProvider` is the parameter name in every task; `_timeProvider` is the field name in every task. `TimeProvider` (the `init` property) differs deliberately in Task 3 Step 6 and is called out there. `FakeTimeProvider` comes from `Microsoft.Extensions.Time.Testing`, which the test project already references as `Microsoft.Extensions.TimeProvider.Testing` 10.5.0.
