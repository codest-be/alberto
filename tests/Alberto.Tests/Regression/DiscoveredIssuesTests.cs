using System.Text.Json;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.Regression;

/// <summary>
/// Regression tests pinning edge-case behaviours discovered during or after the main audit
/// implementation. Each region groups tests by the source issue.
///
/// <para>
/// Every fact here runs. The <c>[Fact(Skip = "...")]</c> convention this comment used to
/// describe — a skipped placeholder standing in for a design gap that could not be verified
/// without a src/ change — was only ever used elsewhere, and those placeholders are gone:
/// each was re-enabled once the underlying fix landed. A regression that cannot be executed
/// does not belong here as a skipped fact; fix it, then pin it.
/// </para>
/// </summary>
public sealed class DiscoveredIssuesTests
{
    /// <summary>
    /// How long a bounded shutdown may take before the test calls it a failure.
    ///
    /// <remarks>
    /// Every loop here bounds its own drain, so a correct <c>StopAsync</c> returns in
    /// milliseconds — this is a failure detector, not a budget. Awaiting it unbounded instead
    /// turns any mutant that removes the drain bound into a hung test rather than a failing one,
    /// which costs a full Stryker timeout window and wedges a CI agent.
    /// See <see href="https://github.com/codest-be/alberto/issues/133">#133</see>.
    /// </remarks>
    /// </summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    // ── Shared helpers ─────────────────────────────────────────────────────────────

    [EventType("discovered-issues-regression-event")]
    private record RegressionEvent(string Label) : IEvent;

    private static readonly string RegressionEventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(RegressionEvent));

    private static EventToPersist CreateRegressionEvent(string label) =>
        new()
        {
            EventType = new EventType(RegressionEventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new RegressionEvent(label)),
        };

    /// <summary>
    /// Minimal <see cref="IEventProcessor"/> that delegates each event to a supplied handler.
    /// </summary>
    private sealed class DelegatingProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task> handler) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { RegressionEventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler(@event, ct);
    }

    /// <summary>
    /// <see cref="ILogger{T}"/> that completes <see cref="Recorded"/> the first time a
    /// <see cref="LogLevel.Critical"/> record is written. <c>ControlLoop</c> writes exactly one,
    /// from the block that records an unrecoverable failure, so awaiting it is an exact
    /// synchronisation point on "the failure has been recorded" — no polling, no delay.
    /// </summary>
    private sealed class CriticalLogSignal<T> : ILogger<T>
    {
        private readonly TaskCompletionSource _recorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Recorded => _recorded.Task;

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical)
                _recorded.TrySetResult();
        }
    }

    // ── EventTag default(T) null backing fields ────────────────────────────────────
    //
    // EventTag stores its string representation in a private `_value` field that is
    // set only by constructors. For default(EventTag), _value is null.
    // Previously ToString() used string interpolation ($"{Concept}:{Id}") which
    // returned ":". After caching the value at construction time, ToString() now
    // returns null for the default instance.
    //
    // See: src/Alberto/EventTag.cs → ToString() / Value property.

    #region EventTag default-struct null fields

    [Fact]
    public void EventTag_Default_ToString_ReturnsNull()
    {
        // Arrange
        var tag = default(EventTag);

        // Act & Assert
        // _value is never set; ToString() returns the null field rather than
        // a formatted string. Callers that assume ToString() is non-null will NPE.
        Assert.Null(tag.ToString());
    }

    [Fact]
    public void EventTag_Default_Value_IsNull()
    {
        var tag = default(EventTag);

        // Value delegates to _value which is null for an uninitialised struct.
        Assert.Null(tag.Value);
    }

    [Fact]
    public void EventTag_Default_ConceptAndId_AreNull()
    {
        var tag = default(EventTag);

        // Both auto-properties backed by unset fields → null.
        // Callers that read Concept or Id without a null-guard will throw
        // downstream (e.g., in LINQ, validation, or dictionary lookups).
        Assert.Null(tag.Concept);
        Assert.Null(tag.Id);
    }

    #endregion


    // ── P1.5: TenantContext hyphen-rejection (breaking change) ─────────────────────
    //
    // The audit tightened the tenant-ID regex from permissive to strict:
    //   ^[a-z][a-z0-9_]{0,62}$
    // This rejects hyphens, uppercase letters, and UUIDs — which were previously
    // accepted. Integrators using hyphenated tenant IDs MUST rename them before
    // upgrading. See upgrade-notes/P1.5.md.
    //
    // Side effect: existing TenancyTests that call SetTenant("tenant-123"),
    // SetTenant("tenant-1"), SetTenant("tenant-2") will now throw ArgumentException.
    // Those tests must be updated to use underscore-delimited IDs (e.g., "tenant_123").
    //
    // See: src/Alberto/Tenancy/TenantContext.cs

    #region P1.5 — TenantContext breaking-change coverage

    [Theory]
    [InlineData("tenant-123")]   // hyphen — now rejected
    [InlineData("tenant-1")]     // hyphen — now rejected
    [InlineData("tenant-2")]     // hyphen — now rejected
    [InlineData("Tenant")]       // uppercase — now rejected
    [InlineData("TENANT_A")]     // uppercase + underscore — still rejected
    [InlineData("123tenant")]    // leading digit — now rejected
    public void TenantContext_SetTenant_PreviouslyValidButNowRejected_Throws(string tenantId)
    {
        // Arrange
        var context = new TenantContext();

        // Act & Assert
        // These IDs passed the old permissive check. The new strict regex rejects them.
        // Any existing application data keyed by these IDs requires migration before
        // upgrading to this version of the library.
        Assert.Throws<ArgumentException>(() => context.SetTenant(tenantId));
    }

    [Theory]
    [InlineData("tenant")]          // simple lowercase — always valid
    [InlineData("tenant_123")]      // underscore delimiter — the migration target
    [InlineData("tenant_1")]        // underscore delimiter — the migration target
    [InlineData("tenant_2")]        // underscore delimiter — the migration target
    [InlineData("t")]               // single letter — minimum valid length
    [InlineData("a1b2c3")]          // lowercase alphanumeric — valid
    public void TenantContext_SetTenant_NewAllowlistFormat_Succeeds(string tenantId)
    {
        // Arrange
        var context = new TenantContext();

        // Act
        context.SetTenant(tenantId);

        // Assert: no exception, TenantId was stored
        Assert.Equal(tenantId, context.TenantId);
    }

    #endregion

    // ── ControlLoop: non-shutdown OperationCanceledException handling ───────────────
    //
    // Cooperative cancellation always carries the token that caused it. ControlLoop's
    // cancellation catch-arms therefore ask `IsShutdownCancellation(ex, ct)` — both that
    // the loop token is cancelled AND that the exception carries a cancelled token —
    // rather than `ct.IsCancellationRequested` alone.
    //
    // An OperationCanceledException carrying no cancelled token is an escaped processor
    // failure, not host shutdown. It must fault the loop and leave the position in flight,
    // whether or not a shutdown happens to be under way at the same moment. Testing only
    // `ct.IsCancellationRequested` conflated the two: a handler that failed while the host
    // was shutting down was logged as a graceful stop and IsFaulted stayed false, which made
    // IsFaulted unusable as an observability signal exactly when it mattered most.
    //
    // The failing event is never MarkCompleted'd either way, so SafeCheckpoint cannot advance
    // past it and at-least-once delivery holds. What the fix changes is the fault *signal*.
    //
    // See: src/Alberto/Subscriptions/ControlLoop.cs → IsShutdownCancellation

    #region Non-shutdown OCE faults the loop without completing the position

    /// <summary>
    /// An escaped OCE cancels the pipeline from the inside, so the loop stops on its own.
    /// The test therefore never cancels first; it waits for the failure to be <i>recorded</i>.
    /// </summary>
    [Fact]
    public async Task Pipelined_NonShutdownOce_FaultsPipeline_AndCheckpointStaysBehindFaultingEvent()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        // Three events; InMemory assigns sequential positions starting at 1.
        await backend.AppendAsync(
            [CreateRegressionEvent("fast-1")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 1
        await backend.AppendAsync(
            [CreateRegressionEvent("non-shutdown-oce")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 2
        await backend.AppendAsync(
            [CreateRegressionEvent("fast-3")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 3

        var processor = new DelegatingProcessor(
            processorId: "non-shutdown-oce-regression",
            handler: (evt, ct) =>
            {
                if (evt.GlobalPosition == 2)
                {
                    // An OperationCanceledException that is NOT sourced from any token.
                    // This must be treated as an escaped failure, not graceful shutdown.
                    throw new OperationCanceledException("simulated non-shutdown OCE");
                }

                return Task.CompletedTask;
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));

        var faultRecorded = new CriticalLogSignal<ControlLoop>();

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required, MaxConcurrency = 3 },
            logger: faultRecorded);

        // ── Act ───────────────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Wait for the failure to be RECORDED. Waiting for handlers to be *entered* is
        // not the same thing and is what made this test flaky: the faulting handler
        // signals on entry, before it throws, so a shutdown triggered off that signal
        // could reach the worker's cancellation arm ahead of the failure.
        //
        // ControlLoop writes its single Critical record from the same finally block that
        // sets IsFaulted, on the line immediately after — and after the final checkpoint
        // flush. Awaiting it is an exact, event-driven signal that both are settled.
        // No polling, no wall-clock delay: an escaped failure cancels the pipeline itself,
        // so this completes without the test cancelling anything.
        await faultRecorded.Recorded.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        await loop.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);

        // ── Assert ────────────────────────────────────────────────────────────────
        var checkpoint = await checkpoints.GetAsync(
            "non-shutdown-oce-regression",
            TestContext.Current.CancellationToken);

        // Position 2 was never MarkCompleted'd, so it stays in flight and
        // SafeCheckpoint = min(in-flight) - 1 = 2 - 1 = 1. The event at position 2 is
        // re-processed after restart (at-least-once is preserved).
        Assert.Equal(1L, checkpoint);
        Assert.True(loop.IsFaulted);
    }

    /// <summary>
    /// The regression the flake was hiding: a handler failure that lands <i>after</i>
    /// shutdown was requested must still be reported as a fault.
    /// </summary>
    /// <remarks>
    /// The ordering here is constructed, not raced — the handler blocks until the test has
    /// cancelled, so shutdown always wins. Before the fix this reported a clean stop.
    /// </remarks>
    [Fact]
    public async Task Pipelined_HandlerFailureCoincidingWithShutdown_IsStillFaulted()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync(
            [CreateRegressionEvent("fails-during-shutdown")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 1

        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "shutdown-coincidence-regression",
            handler: async (evt, ct) =>
            {
                handlerEntered.TrySetResult();

                // Deliberately not ct-aware: the handler is still working when shutdown
                // is requested, and only then does it fail.
                await shutdownRequested.Task;

                throw new OperationCanceledException("simulated non-shutdown OCE");
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Required, MaxConcurrency = 3 });

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await handlerEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // Shutdown first, failure second — the worker token is already cancelled when the
        // handler's OCE reaches the worker's cancellation arm.
        await cts.CancelAsync();
        shutdownRequested.TrySetResult();

        await loop.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);

        // The OCE carries no cancelled token, so it is an escaped failure regardless of
        // the shutdown that is under way.
        Assert.True(loop.IsFaulted);

        // Position 1 never completed, so the checkpoint stays behind it.
        var checkpoint = await checkpoints.GetAsync(
            "shutdown-coincidence-regression",
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, checkpoint ?? 0L);
    }

    /// <summary>
    /// Same guarantee on the sequential path, whose cancellation arm had the identical hole.
    /// </summary>
    [Fact]
    public async Task Sequential_HandlerFailureCoincidingWithShutdown_IsStillFaulted()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync(
            [CreateRegressionEvent("fails-during-shutdown")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 1

        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "sequential-shutdown-coincidence-regression",
            handler: async (evt, ct) =>
            {
                handlerEntered.TrySetResult();
                await shutdownRequested.Task;
                throw new OperationCanceledException("simulated non-shutdown OCE");
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled, MaxConcurrency = 1 });

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        await handlerEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        shutdownRequested.TrySetResult();

        await loop.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None)
            .WaitAsync(StopBudget, TestContext.Current.CancellationToken);

        Assert.True(loop.IsFaulted);

        var checkpoint = await checkpoints.GetAsync(
            "sequential-shutdown-coincidence-regression",
            TestContext.Current.CancellationToken);
        Assert.Equal(0L, checkpoint ?? 0L);
    }

    #endregion

    // ── Outbox claim lifecycle ────────────────────────────────────────────────────

    #region Outbox claim lifecycle

    [Fact]
    public void OutboxStore_ExposesTimeBoundedTokenFencedClaims()
    {
        var claim = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.ClaimPendingAsync));
        var delivered = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.MarkDeliveredAsync));
        var failed = typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.MarkFailedAsync));

        Assert.NotNull(claim);
        Assert.Contains(claim.GetParameters(), p => p.ParameterType == typeof(TimeSpan));
        Assert.NotNull(delivered);
        Assert.Equal(typeof(OutboxClaim), delivered.GetParameters()[0].ParameterType);
        Assert.NotNull(failed);
        Assert.Equal(typeof(OutboxClaim), failed.GetParameters()[0].ParameterType);
    }

    #endregion

}
