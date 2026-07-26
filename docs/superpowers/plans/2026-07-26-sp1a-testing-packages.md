# SP1a: The testing packages — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the two testing packages the rest of the remediation depends on — `Alberto.Dcb.Testing` for people building applications on Alberto, and `Alberto.Dcb.Testing.Xunit` for people implementing backends against it.

**Architecture:** Two new packable projects under `src/`. The customer-facing one carries a module harness, one polling helper, an in-memory outbox store, event-construction helpers and `EventCollector`, and takes no dependency on any test framework. The backend-implementer one carries the abstract contract specifications, and therefore does depend on xunit.v3. Both are wired into `publish-packages.yml`, which means their public surface becomes a versioning commitment on the next push to `main`.

**Tech Stack:** .NET 10.0 / .NET 9.0 multi-target, xUnit v3 3.2.2, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.TimeProvider.Testing` 10.5.0.

## Global Constraints

- Everything under `src/` multi-targets `net9.0;net10.0`. Both new projects do too. `TimeProvider` is available on both.
- `Directory.Build.props` sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — via each csproj — and `<GenerateDocumentationFile>true</GenerateDocumentationFile>` with `CS1591` suppressed in `NoWarn`. Docs on public members are strongly encouraged and warnings are fatal, so a stray unused variable fails the build.
- NuGet versions are centrally managed in `Directory.Packages.props`. A `PackageReference` carries **no** `Version` attribute.
- `tests/Alberto.Dcb.Tests` targets `net10.0` only.
- The suite must stay green. Run `dotnet test` before every commit and never commit red.
- PostgreSQL-backed tests use Testcontainers and require a running Docker daemon locally.
- Branch for this sub-project: `sp1a-testing-packages`, off `main`.

### What this sub-project does NOT do

SP1b migrates the existing 85-file suite onto these packages. SP1a **builds the packages and proves each piece with its own tests**; it rewrites existing tests only where leaving them alone would mean shipping something unverified. Concretely:

- The three divergent polling helpers stay where they are. `Poll.UntilAsync` is new and tested on its own; SP1b deletes the copies.
- The duplicated `OrderCreated` vocabulary across eight test files stays. SP1a adds the canonical `Testing/Events.cs` and uses it only from code SP1a writes.
- `EventCollector` moves out of `tests/` (it has no callers to break — verify with the grep in Task 3).
- The three `FakeBackend` copies **are** consolidated here, in Task 7, because building a canonical superset and leaving all three copies in place ships nothing.

Anything not in that list, leave alone. SP4 is running concurrently in Wave 1 and owns `RebuildCoordinator`, `ProjectionVersions` and `PostgresProjectionRebuildStore`; SP2 owns `CachingCheckpointStore`, `DeadLetterRetryLoop`, the two consume middlewares and `InMemoryDeadLetterStore`. **Do not modify any of those files.** Task 6 derives a specification against `InMemoryDeadLetterStore` without editing it, which is deliberate.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj` | Customer-facing package | Create |
| `src/Alberto.Dcb.Testing/Poll.cs` | The one polling helper | Create |
| `src/Alberto.Dcb.Testing/TestEvents.cs` | Event construction helpers | Create |
| `src/Alberto.Dcb.Testing/EventCollector.cs` | Promoted from `tests/`, `TimeProvider`-driven | Create |
| `src/Alberto.Dcb.Testing/InMemoryOutboxStore.cs` | The missing second `IOutboxStore` | Create |
| `src/Alberto.Dcb.Testing/AlbertoTestHarness.cs` | Module-over-in-memory harness | Create |
| `src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj` | Backend-implementer package | Create |
| `src/Alberto.Dcb.Testing.Xunit/CheckpointStoreSpecification.cs` | Promoted from `tests/` | Create |
| `src/Alberto.Dcb.Testing.Xunit/EventStoreBackendSpecification.cs` | Promoted from `tests/` | Create |
| `src/Alberto.Dcb.Testing.Xunit/StateStoreSpecification.cs` | New — three implementations, no shared spec today | Create |
| `src/Alberto.Dcb.Testing.Xunit/DeadLetterStoreSpecification.cs` | New | Create |
| `src/Alberto.Dcb.Testing.Xunit/OutboxStoreSpecification.cs` | New | Create |
| `tests/Alberto.Dcb.Tests/Testing/Events.cs` | Canonical internal event vocabulary | Create |
| `tests/Alberto.Dcb.Tests/Testing/FakeBackend.cs` | Canonical internal backend descriptor | Create |
| `.github/workflows/publish-packages.yml` | Build, pack and push both packages | Modify |
| `AlbertoV3.slnx` | Register both projects | Modify |

The split at the file level mirrors the split at the package level: nothing in `Alberto.Dcb.Testing` may reference xunit, and the build enforces it because the project has no such reference to resolve.

---

### Task 1: The customer-facing package and its polling helper

**Files:**
- Create: `src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj`
- Create: `src/Alberto.Dcb.Testing/Poll.cs`
- Modify: `AlbertoV3.slnx`
- Modify: `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`
- Test: `tests/Alberto.Dcb.Tests/Testing/PollTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces, in namespace `Alberto.Dcb.Testing`:
  - `public static class Poll` with
    `Task UntilAsync(Func<ValueTask<bool>> condition, string what, TimeSpan? timeout = null, TimeSpan? interval = null, TimeProvider? timeProvider = null, CancellationToken ct = default)`
    and a synchronous-predicate overload
    `Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null, TimeSpan? interval = null, TimeProvider? timeProvider = null, CancellationToken ct = default)`.

  Every later task and SP1b call `Poll.UntilAsync`.

- [ ] **Step 1: Create the branch**

```bash
git switch -c sp1a-testing-packages
```

- [ ] **Step 2: Create the project file**

Create `src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj`, modelled on `src/Alberto.Dcb.InMemory/Alberto.Dcb.InMemory.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

        <!-- Package Metadata -->
        <PackageId>Alberto.Dcb.Testing</PackageId>
        <Title>Alberto DCB Event Store - Testing Helpers</Title>
        <Description>Test helpers for applications built on the Alberto DCB event store: an in-memory module harness, deterministic polling, an in-memory outbox store, and event construction helpers. Framework-neutral - takes no dependency on a test framework.</Description>
        <IsPackable>true</IsPackable>
        <RootNamespace>Alberto.Dcb.Testing</RootNamespace>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\Alberto.Dcb\Alberto.Dcb.csproj" />
        <ProjectReference Include="..\Alberto.Dcb.InMemory\Alberto.Dcb.InMemory.csproj" />
        <ProjectReference Include="..\Alberto.Dcb.Messaging\Alberto.Dcb.Messaging.csproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
        <PackageReference Include="Microsoft.Extensions.Hosting" />
    </ItemGroup>
</Project>
```

There is deliberately no xunit reference and there never will be one. A consumer who wants only `InMemoryOutboxStore` must not be dragged onto a test framework, and that cannot be walked back after the first beta.

- [ ] **Step 3: Write the polling helper's failing tests**

Create `tests/Alberto.Dcb.Tests/Testing/PollTests.cs`:

```csharp
using Alberto.Dcb.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class PollTests
{
    [Fact]
    public async Task UntilAsync_ReturnsAsSoonAsTheConditionHolds()
    {
        var calls = 0;

        await Poll.UntilAsync(
            () => ++calls >= 3,
            "the third call",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UntilAsync_ThrowsTimeoutNamingWhatItWasWaitingFor()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Poll.UntilAsync(
            () => false,
            "a condition that never holds",
            timeout: TimeSpan.FromMilliseconds(50),
            interval: TimeSpan.FromMilliseconds(1),
            ct: TestContext.Current.CancellationToken));

        // Assertion-library-neutral by contract: the package must not reference xunit,
        // so a timeout is an exception, never an Assert.Fail.
        Assert.Contains("a condition that never holds", ex.Message);
    }

    [Fact]
    public async Task UntilAsync_EvaluatesTheConditionOnceBeforeWaitingAtAll()
    {
        // A condition already true must not cost an interval. Tests that poll for
        // work already done are the bulk of the suite's wall-clock time.
        await Poll.UntilAsync(
            () => true,
            "an already-true condition",
            timeout: TimeSpan.FromMilliseconds(1),
            interval: TimeSpan.FromMinutes(1),
            ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UntilAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poll.UntilAsync(
            () => false,
            "anything",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            ct: cts.Token));
    }
}
```

- [ ] **Step 4: Register the project and reference it**

In `AlbertoV3.slnx`, add `<Project Path="src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj" />` alongside the other `src/` entries, matching the file's existing indentation and ordering.

In `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`, add to the `ItemGroup` of `ProjectReference`s, after the `Alberto.Dcb.Telemetry` line:

```xml
        <ProjectReference Include="../../src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj" />
```

- [ ] **Step 5: Run the tests and verify they fail**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~PollTests"
```

Expected: build error `CS0103: The name 'Poll' does not exist in the current context`.

- [ ] **Step 6: Implement the helper**

Create `src/Alberto.Dcb.Testing/Poll.cs`:

```csharp
namespace Alberto.Dcb.Testing;

/// <summary>
/// Waits for an asynchronous system to reach a condition.
/// </summary>
/// <remarks>
/// Alberto's control loop is asynchronous by design, so a test that appends an event and
/// immediately asserts on a projection is asserting on a race. This is the one sanctioned way
/// to wait for it. It throws <see cref="TimeoutException"/> rather than failing an assertion,
/// because this package must stay usable from any test framework.
/// </remarks>
public static class Poll
{
    /// <summary>The timeout used when a caller does not supply one.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The interval between evaluations when a caller does not supply one.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Evaluates <paramref name="condition"/> until it returns <see langword="true"/>.
    /// </summary>
    /// <param name="condition">The condition to wait for. Evaluated immediately, then every <paramref name="interval"/>.</param>
    /// <param name="what">
    /// What is being waited for, in a form that completes the sentence "timed out waiting for ...".
    /// This is the only diagnostic a timeout can offer, so make it specific.
    /// </param>
    /// <param name="timeout">How long to wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="interval">How long to wait between evaluations. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeProvider">
    /// Clock used for both the deadline and the delay. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake to drive a test that must not spend real time.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">The condition did not hold within <paramref name="timeout"/>.</exception>
    public static async Task UntilAsync(
        Func<ValueTask<bool>> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        var clock = timeProvider ?? TimeProvider.System;
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var effectiveInterval = interval ?? DefaultInterval;
        var deadline = clock.GetUtcNow() + effectiveTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Evaluate before delaying: a condition that already holds must cost nothing.
            if (await condition().ConfigureAwait(false))
                return;

            if (clock.GetUtcNow() >= deadline)
                throw new TimeoutException(
                    $"Timed out after {effectiveTimeout} waiting for {what}.");

            await Task.Delay(effectiveInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{ValueTask{bool}}, string, TimeSpan?, TimeSpan?, TimeProvider?, CancellationToken)"/>
    public static Task UntilAsync(
        Func<bool> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return UntilAsync(
            () => new ValueTask<bool>(condition()), what, timeout, interval, timeProvider, ct);
    }
}
```

- [ ] **Step 7: Run the tests and verify they pass**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~PollTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 8: Run the full suite and commit**

```bash
dotnet test
```

Expected: `Passed: 913, Skipped: 2`.

```bash
git add src/Alberto.Dcb.Testing AlbertoV3.slnx tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj tests/Alberto.Dcb.Tests/Testing/PollTests.cs
git commit -m "feat: add Alberto.Dcb.Testing with the one polling helper

Poll.UntilAsync replaces three divergent copies in the suite -- two with
hardcoded timeouts, one returning bool instead of throwing. It takes a
TimeProvider and throws TimeoutException rather than failing an
assertion, because this package must not reference a test framework.

Migrating the existing call sites is SP1b."
```

---

### Task 2: Event construction helpers

**Files:**
- Create: `src/Alberto.Dcb.Testing/TestEvents.cs`
- Test: `tests/Alberto.Dcb.Tests/Testing/TestEventsTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces, in namespace `Alberto.Dcb.Testing`:
  - `public static class TestEvents` with
    `NewEvent<TEvent>(TEvent payload, IEnumerable<EventTag>? tags = null, string? tenantId = null, IReadOnlyDictionary<string, string>? metadata = null) where TEvent : IEvent` returning whatever type the codebase's append path takes (`EventToAppend` or equivalent — read `IEventStoreBackend.AppendAsync`'s parameter type in `src/Alberto.Dcb/` and use exactly that).

  Tasks 3, 4 and 5 build events with it.

- [ ] **Step 1: Read the append signature**

Before writing anything, run:

```bash
grep -rn "AppendAsync" src/Alberto.Dcb/IEventStoreBackend.cs src/Alberto.Dcb/EventStore.cs
```

The element type of the collection `AppendAsync` takes is the return type of `NewEvent<TEvent>`. Use it verbatim. Do **not** invent a new DTO — the point of this helper is to stop tests hand-rolling `EventTypeAttribute.GetEventTypeId`, not to introduce a parallel event shape.

Also read how the existing suite builds one, for the serialization and event-id conventions:

```bash
grep -rn "GetEventTypeId" tests/Alberto.Dcb.Tests --include="*.cs" | head
```

- [ ] **Step 2: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Testing/TestEventsTests.cs`. Adapt the type name in the first assertion to what Step 1 found:

```csharp
using Alberto.Dcb.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class TestEventsTests
{
    [EventType("test-events-probe")]
    private record Probe(string Value) : IEvent;

    [Fact]
    public void NewEvent_ResolvesTheEventTypeIdFromTheAttribute()
    {
        var appended = TestEvents.NewEvent(new Probe("hello"));

        Assert.Equal("test-events-probe", appended.EventType.Id);
    }

    [Fact]
    public void NewEvent_SerializesThePayload()
    {
        var appended = TestEvents.NewEvent(new Probe("hello"));

        Assert.Contains("hello", appended.EventData);
    }

    [Fact]
    public void NewEvent_CarriesTagsAndTenant()
    {
        var appended = TestEvents.NewEvent(
            new Probe("hello"),
            tags: [new EventTag("order", "o-1")],
            tenantId: "acme");

        Assert.Contains(appended.Tags, t => t.Key == "order" && t.Value == "o-1");
        Assert.Equal("acme", appended.TenantId);
    }

    [Fact]
    public void NewEvent_GivesEachEventADistinctId()
    {
        Assert.NotEqual(
            TestEvents.NewEvent(new Probe("a")).Id,
            TestEvents.NewEvent(new Probe("b")).Id);
    }
}
```

If the append DTO carries no `TenantId` or no `Id` — check against Step 1's finding — drop the corresponding test and the corresponding parameter rather than adding the field to the DTO. Widening a shipped core type is out of scope for SP1a.

- [ ] **Step 3: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~TestEventsTests"
```

Expected: build error `CS0103: The name 'TestEvents' does not exist in the current context`.

- [ ] **Step 4: Implement**

Create `src/Alberto.Dcb.Testing/TestEvents.cs`. The shape below assumes Step 1 found `EventToAppend`; substitute the real type and its real constructor:

```csharp
using System.Text.Json;

namespace Alberto.Dcb.Testing;

/// <summary>
/// Builds events for appending, so tests do not hand-roll event type resolution and JSON.
/// </summary>
public static class TestEvents
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds an event ready to append, resolving its type id from
    /// <see cref="EventTypeAttribute"/> and serializing the payload.
    /// </summary>
    /// <param name="payload">The event payload.</param>
    /// <param name="tags">Tags to attach. Defaults to none.</param>
    /// <param name="tenantId">The owning tenant. Null in single-tenant mode.</param>
    /// <param name="metadata">Metadata to attach. Defaults to none.</param>
    public static EventToAppend NewEvent<TEvent>(
        TEvent payload,
        IEnumerable<EventTag>? tags = null,
        string? tenantId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new EventToAppend
        {
            Id = Guid.NewGuid(),
            EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(TEvent))),
            EventData = JsonSerializer.Serialize(payload, SerializerOptions),
            Tags = tags?.ToArray() ?? [],
            TenantId = tenantId,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }
}
```

Match the real type's construction exactly — positional record, object initializer or factory method, whichever it uses.

- [ ] **Step 5: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~TestEventsTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
dotnet test && git add src/Alberto.Dcb.Testing/TestEvents.cs tests/Alberto.Dcb.Tests/Testing/TestEventsTests.cs && git commit -m "feat: add TestEvents.NewEvent

Resolves the event type id from EventTypeAttribute and serializes the
payload, so tests stop doing both by hand."
```

---

### Task 3: Promote EventCollector

**Files:**
- Create: `src/Alberto.Dcb.Testing/EventCollector.cs`
- Delete: `tests/Alberto.Dcb.Tests/Testing/EventCollector.cs`
- Test: `tests/Alberto.Dcb.Tests/Testing/EventCollectorTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `Alberto.Dcb.Testing.EventCollector`, same public surface as the current `Alberto.Dcb.Tests.Testing.EventCollector` plus a `TimeProvider? timeProvider = null` constructor parameter.

- [ ] **Step 1: Confirm it has no callers**

Run:

```bash
grep -rn "EventCollector" tests/Alberto.Dcb.Tests apps tools --include="*.cs" | grep -v "tests/Alberto.Dcb.Tests/Testing/EventCollector.cs"
```

Expected: no output. If there are callers, they only need their `using` swapped from `Alberto.Dcb.Tests.Testing` to `Alberto.Dcb.Testing` — do that in this task rather than leaving the build broken for SP1b.

- [ ] **Step 2: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Testing/EventCollectorTests.cs`:

```csharp
using Alberto.Dcb.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class EventCollectorTests
{
    [EventType("collector-probe")]
    private record Probe(string Value) : IEvent;

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedAfterTheWaitBegan()
    {
        var collector = new EventCollector();
        var envelope = Envelope();

        var waiting = collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken);

        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await waiting);
    }

    [Fact]
    public async Task WaitForProjectedAsync_ReturnsAnEventProjectedBeforeTheWaitBegan()
    {
        var collector = new EventCollector();
        var envelope = Envelope();
        collector.OnProjected("p1", envelope);

        Assert.Same(envelope, await collector.WaitForProjectedAsync(
            "p1", "collector-probe", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForProjectedAsync_TimesOutOnTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var collector = new EventCollector(time);

        var waiting = collector.WaitForProjectedAsync(
            "p1", "never-projected",
            timeout: TimeSpan.FromSeconds(5),
            ct: TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    private static IEventEnvelope Envelope() => /* build one -- see Step 3 */;
}
```

- [ ] **Step 3: Fill in the envelope factory**

`IEventEnvelope` is an interface with eight members (`Id`, `TenantId`, `GlobalPosition`, `EventType`, `Tags`, `EventData`, `Metadata`, `CreatedAt`). Find the concrete type the in-memory backend returns:

```bash
grep -rn "IEventEnvelope" src/Alberto.Dcb.InMemory --include="*.cs"
```

Use that type in `Envelope()`, with `EventType = new EventType("collector-probe")` and any values for the rest. If it is internal to the backend, declare a small `private sealed class StubEnvelope : IEventEnvelope` inside the test file instead — this is a test-local detail and does not belong in the shipped package.

- [ ] **Step 4: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~EventCollectorTests"
```

Expected: build error — `EventCollector` is ambiguous or `Alberto.Dcb.Testing.EventCollector` does not exist.

- [ ] **Step 5: Move the file and inject the clock**

Create `src/Alberto.Dcb.Testing/EventCollector.cs` with the current contents of `tests/Alberto.Dcb.Tests/Testing/EventCollector.cs`, changed in exactly three ways:

Namespace:

```csharp
namespace Alberto.Dcb.Testing;
```

A constructor taking the clock, replacing the implicit one:

```csharp
public sealed class EventCollector
{
    private readonly List<(string ProcessorId, IEventEnvelope Envelope)> _projected = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a collector.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock used for the wait deadline. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake to test timeout behaviour without spending real time.
    /// </param>
    public EventCollector(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;
```

And `WaitForProjectedAsync`'s three `DateTimeOffset.UtcNow` reads become `_timeProvider.GetUtcNow()`, with the semaphore wait taking the clock too:

```csharp
        var deadline = _timeProvider.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(5));

        while (_timeProvider.GetUtcNow() < deadline)
        {
            lock (_projected)
            {
                var match = _projected.FirstOrDefault(p => predicate(p.ProcessorId, p.Envelope));
                if (match.Envelope is not null) return match.Envelope;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;

            // Bounded by the poll interval rather than by `remaining`, so a fake clock that
            // jumps past the deadline is noticed instead of being slept through.
            try { await _signal.WaitAsync(TimeSpan.FromMilliseconds(25), ct); }
            catch (OperationCanceledException) { break; }
        }

        throw new TimeoutException("Timed out waiting for projected event.");
```

Then delete the old file:

```bash
git rm tests/Alberto.Dcb.Tests/Testing/EventCollector.cs
```

- [ ] **Step 6: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~EventCollectorTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 7: Commit**

```bash
dotnet test && git add -A src/Alberto.Dcb.Testing tests/Alberto.Dcb.Tests/Testing && git commit -m "feat: promote EventCollector into Alberto.Dcb.Testing

Its deadline now comes from an injected TimeProvider, and its semaphore
wait is bounded by the poll interval rather than by the remaining time,
so a fake clock jumping past the deadline is noticed rather than slept
through. It had no callers in tests/ -- it was written for consumers and
never used internally."
```

---

### Task 4: InMemoryOutboxStore

`IOutboxStore` has exactly one implementation and it is the PostgreSQL one. A consumer testing outbox behaviour has nothing, while the suite declares its own twice (`OutboxRelayTests.cs:13`, `OutboxHandlerTests.cs:50`).

**Files:**
- Create: `src/Alberto.Dcb.Testing/InMemoryOutboxStore.cs`
- Test: covered by `OutboxStoreSpecification` in Task 6 — this task's own verification is a smoke test.
- Test: `tests/Alberto.Dcb.Tests/Testing/InMemoryOutboxStoreTests.cs` (create)

**Interfaces:**
- Consumes: `IOutboxStore` from `Alberto.Dcb.Messaging`.
- Produces: `public sealed class InMemoryOutboxStore(TimeProvider? timeProvider = null) : IOutboxStore` in namespace `Alberto.Dcb.Testing`, plus `public IReadOnlyList<OutboxEntry> Entries { get; }` for assertions.

  Task 6 derives `OutboxStoreSpecification` against it.

- [ ] **Step 1: Read the interface and the two existing doubles**

```bash
cat src/Alberto.Dcb.Messaging/IOutboxStore.cs
sed -n '13,60p' tests/Alberto.Dcb.Tests/Messaging/OutboxRelayTests.cs
sed -n '50,110p' tests/Alberto.Dcb.Tests/Messaging/OutboxHandlerTests.cs
```

The two doubles are partial — each implements only what its own tests exercise. The package version implements all six methods with the semantics the interface documents, including the claim lease: `ClaimPendingAsync` must consider a `processing` entry eligible again once its lease has expired. That is the behaviour the PostgreSQL store has and neither double does.

- [ ] **Step 2: Write the smoke test**

Create `tests/Alberto.Dcb.Tests/Testing/InMemoryOutboxStoreTests.cs`. The full conformance comes from the specification in Task 6; this covers only the lease, which is the semantic the existing doubles get wrong and therefore the one most likely to be reimplemented wrong here:

```csharp
using Alberto.Dcb.Messaging;
using Alberto.Dcb.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class InMemoryOutboxStoreTests
{
    [Fact]
    public async Task ClaimPendingAsync_DoesNotHandOutAnEntryWhoseLeaseIsStillHeld()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        var first = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct);
        Assert.Single(first);

        time.Advance(TimeSpan.FromSeconds(30));
        var second = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct);

        Assert.Empty(second);
    }

    [Fact]
    public async Task ClaimPendingAsync_ReclaimsAnEntryWhoseLeaseExpired()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct);
        time.Advance(TimeSpan.FromMinutes(2));

        // This is the whole point of the lease, and it is what strands rows when it is
        // missing: a relay that dies between claiming and marking leaves the entry claimed
        // forever otherwise.
        Assert.Single(await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct));
    }

    [Fact]
    public async Task MarkDeliveredAsync_RejectsASupersededClaim()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryOutboxStore(time);
        var ct = TestContext.Current.CancellationToken;
        await store.InsertAsync(NewEntry(), ct);

        var stale = (await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-a", ct))[0];
        time.Advance(TimeSpan.FromMinutes(2));
        await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1), "relay-b", ct);

        Assert.False(await store.MarkDeliveredAsync(stale, ct));
    }

    private static OutboxEntry NewEntry() => /* see Step 3 */;
}
```

- [ ] **Step 3: Fill in the entry factory**

Run:

```bash
grep -rn "record OutboxEntry\|class OutboxEntry\|record OutboxClaim\|class OutboxClaim" src/Alberto.Dcb.Messaging
```

Construct an `OutboxEntry` in `NewEntry()` using whatever shape that shows, with a fresh `Guid` source event id per call so the "duplicate source events are ignored" rule does not swallow the second insert.

- [ ] **Step 4: Run the tests and verify they fail**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~InMemoryOutboxStoreTests"
```

Expected: build error `CS0246: The type or namespace name 'InMemoryOutboxStore' could not be found` — the two existing doubles are `private sealed` nested classes, so they cannot collide with this.

- [ ] **Step 5: Implement**

Create `src/Alberto.Dcb.Testing/InMemoryOutboxStore.cs`. Implement all six `IOutboxStore` methods against a `Dictionary` keyed by the entry's id, under a single `lock`, with:

- `InsertAsync` — ignore an entry whose source event id is already present.
- `ClaimPendingAsync` — order by creation time, take up to `limit`, eligible when status is `pending` **or** status is `processing` and `claimedUntil <= _timeProvider.GetUtcNow()`. Set status `processing`, `claimedBy`, `claimedUntil = now + claimLease`, and bump a per-entry claim token. Return `OutboxClaim`s carrying that token.
- `MarkDeliveredAsync` / `MarkFailedAsync` — return `false` when the entry is gone or its current claim token does not match the one on the claim; otherwise set `delivered` / `failed` (incrementing the retry counter and recording the error) and return `true`.
- `RetryFailedAsync` — reset `failed` entries to `pending`, filtered by `messageType` when supplied.
- `PurgeDeliveredAsync` — remove `delivered` entries created before the cutoff.

Every timestamp comes from the injected `TimeProvider`, never `DateTimeOffset.UtcNow`. That is what makes the lease testable without sleeping, and it is the reason this store belongs in the package rather than being a fourth ad-hoc double.

Give the class and every public member an XML doc comment — `TreatWarningsAsErrors` plus `GenerateDocumentationFile` are on.

- [ ] **Step 6: Run the tests and verify they pass**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~InMemoryOutboxStoreTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 7: Commit**

```bash
dotnet test && git add src/Alberto.Dcb.Testing/InMemoryOutboxStore.cs tests/Alberto.Dcb.Tests/Testing/InMemoryOutboxStoreTests.cs && git commit -m "feat: add InMemoryOutboxStore

IOutboxStore had exactly one implementation and it was the PostgreSQL
one, so a consumer testing outbox behaviour had nothing -- while the
suite declared two partial doubles of its own, neither of which honours
the claim lease. This one does, on an injected TimeProvider.

Replacing the two doubles is SP1b."
```

---

### Task 5: The module harness

**Files:**
- Create: `src/Alberto.Dcb.Testing/AlbertoTestHarness.cs`
- Test: `tests/Alberto.Dcb.Tests/Testing/AlbertoTestHarnessTests.cs` (create)

**Interfaces:**
- Consumes: `Poll` from Task 1, `TestEvents` from Task 2.
- Produces, in namespace `Alberto.Dcb.Testing`:
  - `public sealed class AlbertoTestHarness : IAsyncDisposable`
  - `public static Task<AlbertoTestHarness> StartAsync(string moduleKey, Action<...> configure, Action<IServiceCollection>? configureServices = null, TimeProvider? timeProvider = null, CancellationToken ct = default)` — the second parameter's type is the module-builder delegate `AddAlberto` already takes; read it in Step 1 and use it verbatim.
  - `public IServiceProvider Services { get; }`
  - `public Task AppendAsync<TEvent>(TEvent payload, IEnumerable<EventTag>? tags = null, string? tenantId = null, CancellationToken ct = default) where TEvent : IEvent`
  - `public Task WaitForQuiescenceAsync(TimeSpan? timeout = null, CancellationToken ct = default)`

  SP5 builds the example-app tests on exactly this surface, so the names are a commitment.

- [ ] **Step 1: Read what a module bootstrap actually looks like**

```bash
grep -rn "public static .* AddAlberto" src/Alberto.Dcb/
sed -n '475,520p' tests/Alberto.Dcb.Tests/CommandPipelineTests.cs
grep -rn "WithControlLoop" src/Alberto.Dcb/DcbModuleBuilderExtensions.cs | head
grep -rn "ICheckpointInventory" src/Alberto.Dcb --include="*.cs" | head
```

The harness is a thin wrapper over `HostApplicationBuilder` → `AddAlberto(moduleKey, configure)` → `StartAsync`. It invents nothing: it removes the repeated `ServiceCollection` → `BuildServiceProvider` → hand-rolled poll sequence, and nothing more.

- [ ] **Step 2: Decide how quiescence is detected**

`WaitForQuiescenceAsync` means "every registered processor's checkpoint has reached the store head". `InMemoryCheckpointStore` implements `ICheckpointInventory` (see `src/Alberto.Dcb.InMemory/InMemoryCheckpointStore.cs:10`), which is how the harness enumerates processors without being told about them.

If `ICheckpointInventory` does not expose what you need, resolve the checkpoint store and the head barrier from `Services` and compare — but **do not add a member to `ICheckpointInventory`** to make this convenient. That is a core interface, and widening it for a test helper is out of scope.

- [ ] **Step 3: Write the failing test**

Create `tests/Alberto.Dcb.Tests/Testing/AlbertoTestHarnessTests.cs`:

```csharp
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

public class AlbertoTestHarnessTests
{
    [EventType("harness-order-created")]
    private record HarnessOrderCreated(Guid OrderId, decimal Amount) : IEvent;

    private record OrderTotal
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
    }

    [Fact]
    public async Task AppendThenWaitForQuiescence_LetsAProjectionBeAssertedWithoutPolling()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();

        await using var harness = await AlbertoTestHarness.StartAsync(
            "orders",
            module => module
                .WithInMemory()
                .WithControlLoop(loop => loop.AddProjection(
                    DeclareProjection.For<OrderTotal>("order-total")
                        .On<HarnessOrderCreated>(
                            id: e => e.OrderId.ToString(),
                            apply: (state, e, ctx) => new OrderTotal
                            {
                                OrderId = e.OrderId,
                                Amount = e.Amount
                            })
                        .Build())),
            ct: ct);

        await harness.AppendAsync(new HarnessOrderCreated(orderId, 42m), ct: ct);
        await harness.WaitForQuiescenceAsync(ct: ct);

        var store = harness.Services.GetRequiredKeyedService<IStateStore<OrderTotal>>("order-total");
        var loaded = await store.LoadManyAsync([orderId.ToString()], ct);

        Assert.Equal(42m, loaded[orderId.ToString()].Amount);
    }

    [Fact]
    public async Task WaitForQuiescenceAsync_ThrowsRatherThanReturningWhenNothingCatchesUp()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var harness = await AlbertoTestHarness.StartAsync(
            "stalled",
            module => module.WithInMemory().WithControlLoop(loop => loop.AddProcessor(
                new StalledProcessor())),
            ct: ct);

        await harness.AppendAsync(new HarnessOrderCreated(Guid.NewGuid(), 1m), ct: ct);

        // A harness that returned silently here would turn every downstream assertion into a
        // race that fails somewhere else, days later.
        await Assert.ThrowsAsync<TimeoutException>(
            () => harness.WaitForQuiescenceAsync(TimeSpan.FromMilliseconds(200), ct));
    }
}
```

`StalledProcessor` is an `IEventProcessor` that never completes — copy the shape from `ThrowingProcessor` at `tests/Alberto.Dcb.Tests/Subscriptions/ControlLoopMiddlewareTests.cs:224` and have its process method await `Task.Delay(Timeout.Infinite, ct)`. Adjust `AddProcessor` / `AddProjection` and the keyed-service lookup to whatever Step 1 showed the real registration API to be; the assertions are what matter.

- [ ] **Step 4: Run the test and verify it fails**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~AlbertoTestHarnessTests"
```

Expected: build error `CS0103: The name 'AlbertoTestHarness' does not exist in the current context`.

- [ ] **Step 5: Implement**

Create `src/Alberto.Dcb.Testing/AlbertoTestHarness.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Testing;

/// <summary>
/// A running Alberto module over the in-memory backend, for testing application code.
/// </summary>
/// <remarks>
/// Alberto's control loop is asynchronous, so appending an event and asserting on a projection
/// in the next line asserts on a race. The harness exists to make the correct sequence -- append,
/// wait for quiescence, assert -- shorter than the incorrect one.
/// </remarks>
public sealed class AlbertoTestHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _moduleKey;
    private readonly TimeProvider _timeProvider;

    private AlbertoTestHarness(IHost host, string moduleKey, TimeProvider timeProvider)
    {
        _host = host;
        _moduleKey = moduleKey;
        _timeProvider = timeProvider;
    }

    /// <summary>The running host's services. Resolve module services with the module key.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The module key this harness was started with.</summary>
    public string ModuleKey => _moduleKey;

    /// <summary>Starts a module and its control loop.</summary>
    /// <param name="moduleKey">The module key, used for every keyed resolution.</param>
    /// <param name="configure">Configures the module exactly as production code would.</param>
    /// <param name="configureServices">Additional registrations, applied before the module.</param>
    /// <param name="timeProvider">Clock used for quiescence waits. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AlbertoTestHarness> StartAsync(
        string moduleKey,
        Action<IDcbModuleBuilder> configure,
        Action<IServiceCollection>? configureServices = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddAlberto(moduleKey, configure);

        var host = builder.Build();
        await host.StartAsync(ct).ConfigureAwait(false);

        return new AlbertoTestHarness(host, moduleKey, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Appends one event to the module's store.</summary>
    public async Task AppendAsync<TEvent>(
        TEvent payload,
        IEnumerable<EventTag>? tags = null,
        string? tenantId = null,
        CancellationToken ct = default)
        where TEvent : IEvent
    {
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(_moduleKey);
        await store.AppendAsync(
            [TestEvents.NewEvent(payload, tags, tenantId)], cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until every processor's checkpoint has reached the store head.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Processing did not catch up. Deliberately loud: returning quietly would push the failure
    /// into an unrelated assertion later.
    /// </exception>
    public Task WaitForQuiescenceAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => Poll.UntilAsync(
            IsQuiescentAsync,
            $"module '{_moduleKey}' to finish processing",
            timeout,
            timeProvider: _timeProvider,
            ct: ct);

    private async ValueTask<bool> IsQuiescentAsync()
    {
        // Implement against the interfaces found in Step 2. Every processor named by the
        // checkpoint inventory must have a checkpoint at or past the store head.
        throw new NotImplementedException("See Step 2.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}
```

Replace `IsQuiescentAsync`'s body with the real check from Step 2, and `IDcbModuleBuilder` with the real builder type from Step 1. Everything else stands as written.

- [ ] **Step 6: Run the test and verify it passes**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~AlbertoTestHarnessTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
dotnet test && git add src/Alberto.Dcb.Testing/AlbertoTestHarness.cs tests/Alberto.Dcb.Tests/Testing/AlbertoTestHarnessTests.cs && git commit -m "feat: add AlbertoTestHarness

Stands up a module over the in-memory backend, appends, and waits for
the control loop to reach quiescence -- replacing the ad-hoc
ServiceCollection/BuildServiceProvider/hand-rolled-poll sequence the
suite repeats. Quiescence timing out throws rather than returning
quietly, so the failure lands where the cause is.

SP5 builds the example-app tests on this surface."
```

---

### Task 6: The xunit package and its specifications

**Files:**
- Create: `src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj`
- Create: `src/Alberto.Dcb.Testing.Xunit/CheckpointStoreSpecification.cs`
- Create: `src/Alberto.Dcb.Testing.Xunit/EventStoreBackendSpecification.cs`
- Create: `src/Alberto.Dcb.Testing.Xunit/StateStoreSpecification.cs`
- Create: `src/Alberto.Dcb.Testing.Xunit/DeadLetterStoreSpecification.cs`
- Create: `src/Alberto.Dcb.Testing.Xunit/OutboxStoreSpecification.cs`
- Delete: `tests/Alberto.Dcb.Tests/Subscriptions/CheckpointStoreSpecification.cs`
- Delete: `tests/Alberto.Dcb.Tests/EventStoreBackendSpecification.cs`
- Modify: `AlbertoV3.slnx`, `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`
- Test: derivations under `tests/Alberto.Dcb.Tests/` (create, listed in Step 6)

**Interfaces:**
- Consumes: `InMemoryOutboxStore` from Task 4.
- Produces, in namespace `Alberto.Dcb.Testing.Xunit`, five public abstract classes. Each declares the factory its derived class must supply:
  - `CheckpointStoreSpecification` — `protected abstract Task<ICheckpointStore> CreateStore();`
  - `EventStoreBackendSpecification` — same abstract members it has today, unchanged.
  - `StateStoreSpecification<TState>` — `protected abstract Task<IStateStore<TState>> CreateStore();` plus `protected abstract TState NewState(string documentId, int marker);` and `protected abstract int MarkerOf(TState state);`
  - `DeadLetterStoreSpecification` — `protected abstract Task<IDeadLetterStore> CreateStore();`
  - `OutboxStoreSpecification` — `protected abstract Task<IOutboxStore> CreateStore();` plus `protected abstract TimeProvider TimeProvider { get; }`

- [ ] **Step 1: Create the project**

Create `src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

        <!-- Package Metadata -->
        <PackageId>Alberto.Dcb.Testing.Xunit</PackageId>
        <Title>Alberto DCB Event Store - Backend Conformance Suite</Title>
        <Description>xUnit contract specifications for Alberto DCB backend implementations. Derive from these to run Alberto's own conformance suite against your event store, checkpoint store, state store, dead-letter store or outbox.</Description>
        <IsPackable>true</IsPackable>
        <RootNamespace>Alberto.Dcb.Testing.Xunit</RootNamespace>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\Alberto.Dcb\Alberto.Dcb.csproj" />
        <ProjectReference Include="..\Alberto.Dcb.Messaging\Alberto.Dcb.Messaging.csproj" />
        <ProjectReference Include="..\Alberto.Dcb.Testing\Alberto.Dcb.Testing.csproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="xunit.v3" />
    </ItemGroup>
</Project>
```

Add it to `AlbertoV3.slnx` and add a `ProjectReference` to it from `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`, both alongside the `Alberto.Dcb.Testing` entries from Task 1.

- [ ] **Step 2: Move the two existing specifications**

```bash
git mv tests/Alberto.Dcb.Tests/Subscriptions/CheckpointStoreSpecification.cs src/Alberto.Dcb.Testing.Xunit/CheckpointStoreSpecification.cs
git mv tests/Alberto.Dcb.Tests/EventStoreBackendSpecification.cs src/Alberto.Dcb.Testing.Xunit/EventStoreBackendSpecification.cs
```

In both files change the namespace to `Alberto.Dcb.Testing.Xunit`, and add `using Alberto.Dcb.Testing.Xunit;` to the two existing derivations:

- `tests/Alberto.Dcb.Tests/InMemoryEventStoreBackendTests.cs:9`
- `tests/Alberto.Dcb.Tests/PostgresEventStoreBackendTests.cs:13`

plus whatever derives `CheckpointStoreSpecification` — find them with:

```bash
grep -rn "CheckpointStoreSpecification" tests --include="*.cs"
```

Both moved files are now public API. Give every protected and public member an XML doc comment; a third party deriving from these has nothing else to go on. Anything the specifications reference that lives in `tests/` must move with them or be inlined — if either file uses a test-local event type, add that type to the specification file itself as a nested `protected` record, because it is now part of the contract.

- [ ] **Step 3: Verify the move is behaviour-neutral**

Run:

```bash
dotnet test
```

Expected: `Passed: 918, Skipped: 2` — the same tests as before Task 6, running from their new home. A drop in the count means a derivation lost its base class; find it before continuing.

Commit this move on its own, so the review of the new specifications is not tangled up with a file rename:

```bash
git add -A && git commit -m "refactor: promote the two existing specifications into Alberto.Dcb.Testing.Xunit

Pure move plus namespace change. A third party writing a backend can now
run Alberto's own conformance suite against it."
```

- [ ] **Step 4: Write StateStoreSpecification**

`IStateStore<TState>` has three implementations — `InMemoryStateStore<TState>`, `PostgresStateStore<TState>` and `EfStateStore<TEntity, TDbContext>` — and no shared specification. Create `src/Alberto.Dcb.Testing.Xunit/StateStoreSpecification.cs`, following the shape of the `CheckpointStoreSpecification` you just moved (`public abstract class`, a per-run unique id, `protected abstract` factory, `[Fact]` methods using `TestContext.Current.CancellationToken`):

```csharp
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Testing.Xunit;

/// <summary>
/// The contract every <see cref="IStateStore{TState}"/> implementation must satisfy.
/// Derive from it once per implementation.
/// </summary>
/// <typeparam name="TState">The state type under test.</typeparam>
public abstract class StateStoreSpecification<TState>
{
    /// <summary>A document id unique to this test run, so implementations may share a database.</summary>
    protected string DocumentId { get; } = $"doc-{Guid.NewGuid():N}";

    /// <summary>Creates the store under test.</summary>
    protected abstract Task<IStateStore<TState>> CreateStore();

    /// <summary>Builds a state carrying a distinguishable <paramref name="marker"/>.</summary>
    protected abstract TState NewState(string documentId, int marker);

    /// <summary>Reads back the marker <see cref="NewState"/> put in.</summary>
    protected abstract int MarkerOf(TState state);

    [Fact]
    public async Task LoadManyAsync_ReturnsNothingForAnUnknownDocument()
    {
        var store = await CreateStore();

        var loaded = await store.LoadManyAsync([DocumentId], TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task ApplyChangesAsync_ThenLoadManyAsync_RoundTripsTheState()
    {
        var store = await CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 1) }, [], ct);

        var loaded = await store.LoadManyAsync([DocumentId], ct);

        Assert.Equal(1, MarkerOf(loaded[DocumentId]));
    }

    [Fact]
    public async Task ApplyChangesAsync_UpsertsRatherThanDuplicating()
    {
        var store = await CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 1) }, [], ct);
        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 2) }, [], ct);

        var loaded = await store.LoadManyAsync([DocumentId], ct);

        Assert.Single(loaded);
        Assert.Equal(2, MarkerOf(loaded[DocumentId]));
    }

    [Fact]
    public async Task ApplyChangesAsync_Deletes()
    {
        var store = await CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 1) }, [], ct);
        await store.ApplyChangesAsync(new Dictionary<string, TState>(), [DocumentId], ct);

        Assert.Empty(await store.LoadManyAsync([DocumentId], ct));
    }

    [Fact]
    public async Task ApplyChangesAsync_AppliesAnUpsertAndADeleteInOneCall()
    {
        var store = await CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var other = $"{DocumentId}-other";

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 1) }, [], ct);
        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [other] = NewState(other, 2) }, [DocumentId], ct);

        var loaded = await store.LoadManyAsync([DocumentId, other], ct);

        Assert.Single(loaded);
        Assert.Equal(2, MarkerOf(loaded[other]));
    }

    [Fact]
    public async Task LoadManyAsync_ReturnsOnlyTheDocumentsThatExist()
    {
        var store = await CreateStore();
        var ct = TestContext.Current.CancellationToken;

        await store.ApplyChangesAsync(
            new Dictionary<string, TState> { [DocumentId] = NewState(DocumentId, 1) }, [], ct);

        var loaded = await store.LoadManyAsync([DocumentId, $"{DocumentId}-absent"], ct);

        // The interface says "found documents", so a missing one is an absent key, not a default
        // value -- a distinction the projection pipeline relies on to tell create from update.
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey(DocumentId));
    }
}
```

- [ ] **Step 5: Write DeadLetterStoreSpecification and OutboxStoreSpecification**

Create the two remaining files in the same shape. For each, read the interface first and write one `[Fact]` per documented behaviour:

```bash
cat src/Alberto.Dcb/Subscriptions/IDeadLetterStore.cs
cat src/Alberto.Dcb.Messaging/IOutboxStore.cs
```

`DeadLetterStoreSpecification` must cover, at minimum: `CountAsync` on an empty store, add-then-count, `ClearAsync`, `MarkForRetryAsync` followed by `ClaimRetryRequestedAsync`, `CompleteRetryAsync` removing the entry, and `AbandonRetryAsync` returning it to the pool. `InMemoryDeadLetterStore` has zero direct tests today, so these are its first.

`OutboxStoreSpecification` must cover the six methods and, specifically, the three lease behaviours from Task 4 Step 2 — held lease, expired lease, superseded claim — since those are the semantics the ad-hoc doubles get wrong. It exposes `protected abstract TimeProvider TimeProvider { get; }` so a derivation over the in-memory store can supply a fake and one over PostgreSQL can supply `TimeProvider.System`; where the specification needs time to pass, branch on whether the provider is a fake rather than sleeping unconditionally, or — better — write those three tests only against a controllable clock and document that in the class summary.

- [ ] **Step 6: Derive them**

Create one derivation per implementation, under `tests/Alberto.Dcb.Tests/`:

- `Subscriptions/InMemoryStateStoreSpecificationTests.cs` — `StateStoreSpecification<OrderTotal>` over `InMemoryStateStore<OrderTotal>`
- `Postgres/PostgresStateStoreSpecificationTests.cs` — over `PostgresStateStore<OrderTotal>`, with the Postgres fixture the neighbouring Postgres tests use
- `EntityFramework/EfStateStoreSpecificationTests.cs` — over `EfStateStore<...>`. **If `EfStateStore` cannot satisfy the contract** — it is entity-backed and may not honour arbitrary document ids — do not weaken the specification. Skip the derivation, and say so in the PR description with the reason. A specification bent to fit its hardest implementation certifies nothing.
- `Subscriptions/InMemoryDeadLetterStoreSpecificationTests.cs` — over `InMemoryDeadLetterStore`
- `Postgres/PostgresDeadLetterStoreSpecificationTests.cs` — over the PostgreSQL dead-letter store
- `Testing/InMemoryOutboxStoreSpecificationTests.cs` — over Task 4's `InMemoryOutboxStore`, supplying a `FakeTimeProvider`
- `Postgres/PostgresOutboxStoreSpecificationTests.cs` — over `PostgresOutboxStore`, supplying `TimeProvider.System`

Each is a few lines: derive, implement the factory, done.

`OrderTotal` comes from Task 7's `tests/Alberto.Dcb.Tests/Testing/Events.cs`. If Task 7 has not run yet, declare it inline and fold it into `Events.cs` there.

- [ ] **Step 7: Run the tests**

Run:

```bash
dotnet test
```

Expected: a substantial rise in the count — six or seven derivations times five to eight facts each. Record the new total; it is the baseline for Step 8's commit message.

**Expect real failures here.** These are first-ever tests for `InMemoryDeadLetterStore` and first-ever shared tests for the three state stores. A failure is a finding, not a broken plan. For each one: decide whether the implementation is wrong or the specification over-specifies, fix the correct side, and note it in the PR description. Do not delete a failing fact to get green.

- [ ] **Step 8: Commit**

```bash
git add -A src/Alberto.Dcb.Testing.Xunit tests/Alberto.Dcb.Tests && git commit -m "feat: add state store, dead-letter and outbox specifications

Three interfaces with multiple implementations and no shared contract
test between them. InMemoryDeadLetterStore had no direct tests at all.

Each specification is derived once per implementation, so a third party
writing a backend runs the same suite Alberto runs against its own."
```

---

### Task 7: Consolidate the internal helpers

The canonical `FakeBackend` and the canonical event vocabulary stay in `tests/` — consumers do not implement backend descriptors, and the example apps have real domain events and must not borrow test ones.

**Files:**
- Create: `tests/Alberto.Dcb.Tests/Testing/FakeBackend.cs`
- Create: `tests/Alberto.Dcb.Tests/Testing/Events.cs`
- Modify: `tests/Alberto.Dcb.Tests/Configuration/AlbertoModuleValidatorTests.cs:12`
- Modify: `tests/Alberto.Dcb.Tests/Configuration/ModuleDefinitionTests.cs:13`
- Modify: `tests/Alberto.Dcb.Tests/Configuration/UnknownConfigurationKeyTests.cs:23`

**Interfaces:**
- Consumes: nothing.
- Produces, in namespace `Alberto.Dcb.Tests.Testing`:
  - `internal sealed class FakeBackend : IAlbertoBackendDescriptor` with settable `SupportsTenancy`, injectable `AlbertoValidationFailure[]`, and `Registered` / `TenancyAtRegistration` tracking.
  - `Events.cs` holding `OrderCreated`, `OrderConfirmed`, `OrderCancelled`, `OrderNoteAdded` and the `OrderTotal` / `OrderSummary` states, each with its `[EventType]` id.

- [ ] **Step 1: Read the three copies**

```bash
sed -n '12,60p' tests/Alberto.Dcb.Tests/Configuration/AlbertoModuleValidatorTests.cs
sed -n '13,60p' tests/Alberto.Dcb.Tests/Configuration/ModuleDefinitionTests.cs
sed -n '23,70p' tests/Alberto.Dcb.Tests/Configuration/UnknownConfigurationKeyTests.cs
```

The canonical version is a superset: everything all three do, with the differences turned into constructor parameters defaulting to the most common variant. It must not be narrower than any copy in any respect.

- [ ] **Step 2: Write the canonical FakeBackend**

Create `tests/Alberto.Dcb.Tests/Testing/FakeBackend.cs` with an `internal sealed class FakeBackend(bool supportsTenancy = true, params AlbertoValidationFailure[] failures) : IAlbertoBackendDescriptor`, keeping the primary-constructor shape `AlbertoModuleValidatorTests` already uses. Add the tracking the other two need:

```csharp
    /// <summary>Whether registration was invoked, and with what tenancy in effect.</summary>
    public bool Registered { get; private set; }

    /// <summary>
    /// Tenancy as seen at registration time. Order-dependent: a module that calls WithTenancy()
    /// after UseBackend() must still register a tenant-aware backend, which is exactly what
    /// ModuleDefinitionTests asserts.
    /// </summary>
    public bool? TenancyAtRegistration { get; private set; }
```

set from the descriptor's registration method.

- [ ] **Step 3: Delete the three copies**

Remove the nested `FakeBackend` from each of the three files and add `using Alberto.Dcb.Tests.Testing;`. Change nothing else in those files — no test bodies, no assertions.

- [ ] **Step 4: Run the configuration tests**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --filter "FullyQualifiedName~Configuration"
```

Expected: PASS, with the same count as before this task. A behaviour change here means the canonical version is narrower than one of the copies; widen it rather than adjusting the test.

- [ ] **Step 5: Write the canonical event vocabulary**

Create `tests/Alberto.Dcb.Tests/Testing/Events.cs` holding the `OrderCreated` / `OrderConfirmed` / `OrderCancelled` / `OrderNoteAdded` records and the `OrderTotal` / `OrderSummary` states, copied from `tests/Alberto.Dcb.Tests/Subscriptions/ProjectionSpecificationTests.cs:15-36` — that copy is the most complete of the eight.

**Do not migrate the eight existing files onto it.** That is SP1b, and doing it here guarantees a merge conflict with SP1b's own sweep. This file exists so Task 6's derivations have a shared `OrderTotal`, and so SP1b has a target to migrate toward.

- [ ] **Step 6: Point Task 6's derivations at it**

Replace the inline `OrderTotal` declared in Task 6 Step 6 with `Alberto.Dcb.Tests.Testing.OrderTotal`. If Task 6 already used it, nothing to do.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet test
```

Expected: the same count as Task 6 Step 7.

```bash
git add -A tests/Alberto.Dcb.Tests && git commit -m "refactor: one FakeBackend, one test event vocabulary

FakeBackend had three divergent copies across the Configuration tests;
the canonical one is a superset of all three, so no test changed
behaviour. Events.cs is the target the eight duplicated OrderCreated
declarations migrate onto in SP1b -- migrating them here would collide
with that sweep."
```

---

### Task 8: Ship both packages

**Files:**
- Modify: `.github/workflows/publish-packages.yml`

**Interfaces:**
- Consumes: both projects from Tasks 1 and 6.
- Produces: `Alberto.Dcb.Testing` and `Alberto.Dcb.Testing.Xunit` on GitHub Packages from the next push to `main`.

- [ ] **Step 1: Add both to the build step**

In `.github/workflows/publish-packages.yml`, in the `Build libraries` step, add after the `Alberto.Dcb.Telemetry` line:

```yaml
          dotnet build src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj -c Release
          dotnet build src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj -c Release
```

- [ ] **Step 2: Add both to the pack loop**

In the `Pack` step, extend the `for proj in ...` list with `Alberto.Dcb.Testing Alberto.Dcb.Testing.Xunit`, so it reads:

```yaml
          for proj in Alberto.Dcb Alberto.Dcb.Commands Alberto.Dcb.EntityFramework Alberto.Dcb.InMemory Alberto.Dcb.Messaging Alberto.Dcb.Postgres Alberto.Dcb.Postgres.Messaging Alberto.Dcb.Telemetry Alberto.Dcb.Testing Alberto.Dcb.Testing.Xunit; do
```

The push step already globs `artifacts/Alberto.Dcb*.nupkg`, so both are pushed without further change. Confirm that by reading the step rather than assuming.

- [ ] **Step 3: Verify the workflow parses and both projects pack**

Run:

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/publish-packages.yml')); print('publish-packages.yml parses')"
```

Expected: `publish-packages.yml parses`

Then verify packing locally, which is the only way to catch a missing `Description` or a broken `ProjectReference` before it reaches `main`:

```bash
dotnet pack src/Alberto.Dcb.Testing/Alberto.Dcb.Testing.csproj -c Release --version-suffix local -o /private/tmp/claude-501/-Users-bjorn-dev-AlbertoV3/da1b5402-71ff-41f6-abb3-29a96b284e26/scratchpad/pack
dotnet pack src/Alberto.Dcb.Testing.Xunit/Alberto.Dcb.Testing.Xunit.csproj -c Release --version-suffix local -o /private/tmp/claude-501/-Users-bjorn-dev-AlbertoV3/da1b5402-71ff-41f6-abb3-29a96b284e26/scratchpad/pack
```

Expected: two `.nupkg` files and two `.snupkg` files, no warnings.

- [ ] **Step 4: Confirm the framework-neutrality claim**

The whole reason for two packages is that `Alberto.Dcb.Testing` does not drag a consumer onto xunit. Verify it rather than trusting the csproj:

```bash
unzip -p /private/tmp/claude-501/-Users-bjorn-dev-AlbertoV3/da1b5402-71ff-41f6-abb3-29a96b284e26/scratchpad/pack/Alberto.Dcb.Testing.*.nupkg Alberto.Dcb.Testing.nuspec | grep -i xunit
```

Expected: no output. Any match means a transitive xunit dependency leaked in and the split is not real — fix it before merging, because it cannot be walked back after the first beta.

- [ ] **Step 5: Run the full suite and commit**

```bash
dotnet test
```

Expected: the same count as Task 7.

```bash
git add .github/workflows/publish-packages.yml && git commit -m "ci: publish the two testing packages

Both ship as betas from the next push to main touching src/**, so their
public surface is a versioning commitment from that point on.

Verified Alberto.Dcb.Testing's nuspec carries no xunit dependency: the
package split only means anything if a consumer who wants
InMemoryOutboxStore is not forced onto a test framework."
```

- [ ] **Step 6: Push and open the PR**

```bash
git push -u origin sp1a-testing-packages
```

Open a PR titled `SP1a: the testing packages`. The description must list, because reviewers cannot infer them from the diff:

1. The full public surface of both packages — this is the versioning commitment.
2. Any specification fact that failed in Task 6 Step 7, what it revealed, and which side you fixed.
3. Whether `EfStateStoreSpecificationTests` was written or skipped, with the reason.
4. What SP1b still has to migrate: three polling-helper copies, two outbox doubles, eight duplicated event vocabularies.

---

## Self-Review

**Spec coverage.** The spec's SP1a section names, for the customer-facing package: a module test harness (Task 5), one polling helper that throws rather than asserting (Task 1), `InMemoryOutboxStore` (Task 4), event construction helpers (Task 2), `EventCollector` with an injected `TimeProvider` (Task 3), and no test-framework dependency (Task 1 Step 2, verified in Task 8 Step 4). For the backend-implementer package: `StateStoreSpecification`, `DeadLetterStoreSpecification`, `OutboxStoreSpecification` (Tasks 6 Steps 4-5), plus `EventStoreBackendSpecification` and `CheckpointStoreSpecification` promoted out of `tests/` (Task 6 Step 2). For the internal category: the canonical `FakeBackend` superset with settable `SupportsTenancy`, injectable failures and `Registered`/`TenancyAtRegistration` tracking (Task 7), and the collapsed event vocabulary (Task 7 Step 5). Both packages enter `publish-packages.yml` (Task 8).

The spec's "validator scaffolding, the config-test `ProcessorDeclaration` builders" are named as staying internal but are not consolidated by any task. That is deliberate and it is a gap against a literal reading: consolidating them is a migration across the config tests, which is SP1b's shape, and the spec does not say SP1a must move them — only that they do not ship. Flagged here rather than silently dropped.

**Placeholder scan.** Several steps deliberately read a signature before writing against it — Task 2 Step 1 (the append DTO), Task 3 Step 3 (the envelope type), Task 4 Step 3 (`OutboxEntry`), Task 5 Steps 1-2 (the module builder delegate and the quiescence check). In each case the surrounding code is given complete and only the type name is deferred, with the exact command that resolves it. That is a real limitation of this plan: those four types were not read while writing it. `AlbertoTestHarness.IsQuiescentAsync` ships as `throw new NotImplementedException("See Step 2.")` in the plan text and must not ship that way in code — Task 5 Step 5's closing paragraph says so explicitly, and Task 5 Step 3's second test fails if it does.

Task 6 Step 5 describes `DeadLetterStoreSpecification` and `OutboxStoreSpecification` by enumerating required facts rather than giving their bodies. Given at the same fidelity as `StateStoreSpecification` they would run to several hundred lines against interfaces whose exact parameter lists were not read; the enumeration names every behaviour that must be covered, which is the part a reviewer can check.

**Type consistency.** `Poll.UntilAsync` has one signature, defined in Task 1 and called in Task 5. `TestEvents.NewEvent` is defined in Task 2 and called in Task 5's `AppendAsync`. `InMemoryOutboxStore(TimeProvider?)` is defined in Task 4 and derived against in Task 6 Step 6. `OrderTotal` is used in Task 6 Step 6 and defined in Task 7 Step 5, with Task 6 Step 6 and Task 7 Step 6 both naming the ordering dependency. `StateStoreSpecification<TState>`'s three abstract members are identical in its Interfaces block, its definition and its described derivations. The namespace is `Alberto.Dcb.Testing` for the first package and `Alberto.Dcb.Testing.Xunit` for the second throughout.

**Task ordering.** Task 6 depends on Task 4 (`InMemoryOutboxStore`) and softly on Task 7 (`OrderTotal`); Task 7 Step 6 closes that loop either way. Task 8 depends on Tasks 1 and 6 for the projects to exist. Everything else is independent, so Tasks 2, 3 and 4 can run in any order.
