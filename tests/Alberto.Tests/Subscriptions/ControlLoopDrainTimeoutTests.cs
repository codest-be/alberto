using System.Diagnostics;
using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for the bounded shutdown drain (<c>ControlLoopOptions.DrainTimeout</c>).
///
/// <para>
/// <b>Pre-fix behaviour:</b> <c>StopAsync</c> awaited the loop task with no bound. A handler
/// that ignores its <see cref="CancellationToken"/> — a blocking HTTP call, a driver that
/// swallows cancellation, a <c>lock</c> held by another thread — therefore blocked shutdown
/// forever. Under leasing that is worse than a slow exit: the replica keeps holding its
/// processor lease while the host refuses to terminate, so a blue/green handoff stalls until
/// the orchestrator SIGKILLs the pod.
/// </para>
///
/// <para>
/// <b>Fix:</b> every long-lived loop (<see cref="ControlLoop"/>, <see cref="EventStoreHead"/>,
/// <c>DeadLetterRetryLoop</c>) bounds its shutdown wait with the configured drain timeout and
/// logs a warning when it elapses.
/// </para>
///
/// <para>
/// <b>Correctness of abandoning:</b> abandoning the wait never loses an event. A worker that
/// never returns also never calls <c>MarkCompleted</c>, so <c>PositionWatermark.SafeCheckpoint</c>
/// stays behind its position and the final flush cannot checkpoint past unprocessed work — the
/// event is simply re-delivered on the next start (at-least-once, as always).
/// </para>
/// </summary>
public sealed class ControlLoopDrainTimeoutTests
{
    /// <summary>
    /// Short enough to keep the suite fast, long enough that it cannot be mistaken for the
    /// scheduling noise the assertions tolerate.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Upper bound asserted on a bounded stop. Generously above <see cref="DrainTimeout"/> so a
    /// loaded CI agent cannot fail the test, but far below the "hangs forever" behaviour it guards.
    /// </summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-event (non-batched, serial) dispatch. <see cref="ProcessorExecutionOptions.Default"/>
    /// requires batching, which the lightweight test processors below do not implement.
    /// </summary>
    private static readonly ProcessorExecutionOptions SequentialExecution =
        new() { BatchingMode = ProcessorBatchingMode.Disabled };

    /// <summary>
    /// Polls until the processor's checkpoint reaches <paramref name="expected"/>. Used to reach a
    /// known-good state before the interesting part of a test, so the assertions afterwards do not
    /// race the loop's own progress.
    /// </summary>
    private static async Task WaitForCheckpointAsync(
        InMemoryCheckpointStore checkpoints, string processorId, long expected, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await checkpoints.GetAsync(processorId, ct) >= expected) return;
            await Task.Delay(10, ct);
        }

        Assert.Fail($"Checkpoint for '{processorId}' never reached {expected}.");
    }

    // ── shared event type ──────────────────────────────────────────────────────────

    [EventType("drain-timeout-event")]
    private record DrainTimeoutEvent(string Label) : IEvent;

    private static readonly string EventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(DrainTimeoutEvent));

    private static EventToPersist CreateEvent(string label) =>
        new()
        {
            EventType = new EventType(EventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new DrainTimeoutEvent(label)),
        };

    // ── test doubles ──────────────────────────────────────────────────────────────

    /// <summary>Delegates every event to a supplied handler.</summary>
    private sealed class DelegatingProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task> handler) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }

        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler(@event, ct);
    }

    /// <summary>
    /// Head backend that answers the start-up warm-up normally and then stalls every later
    /// refresh, ignoring cancellation — the "driver that swallows the token" scenario.
    /// </summary>
    private sealed class StallingHeadBackend : IEventStoreHeadBackend
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Completes once a refresh is verifiably stuck inside the backend.</summary>
        public Task WhenStalled => _stalled.Task;

        /// <summary>Lets the stalled call return, so the test leaves no task running.</summary>
        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<long>> GetPositionsAsync(
            long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        {
            // First call is EventStoreHead's warm-up inside StartAsync — it must return.
            if (Interlocked.Increment(ref _calls) == 1)
                return [];

            _stalled.TrySetResult();
            await _release.Task; // deliberately not observing cancellationToken
            return [];
        }
    }

    /// <summary>
    /// Claimable dead-letter store whose first claim attempt stalls, ignoring cancellation.
    /// </summary>
    private sealed class StallingDeadLetterStore : IClaimableDeadLetterStore
    {
        private readonly InMemoryDeadLetterStore _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenStalled => _stalled.Task;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId, int batchSize, TimeSpan leaseDuration,
            string claimedBy, CancellationToken ct = default)
        {
            _stalled.TrySetResult();
            await _release.Task; // deliberately not observing ct
            return [];
        }

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
            => _inner.StoreAsync(entry, ct);

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
            string processorId, string? tenantId = null, int limit = 100, CancellationToken ct = default)
            => _inner.GetAsync(processorId, tenantId, limit, ct);

        public Task<int> CountAsync(string processorId, CancellationToken ct = default)
            => _inner.CountAsync(processorId, ct);

        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
            => _inner.CompleteRetryAsync(claim, ct);

        public Task ClearAsync(string processorId, CancellationToken ct = default)
            => _inner.ClearAsync(processorId, ct);

        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
            => _inner.MarkForRetryAsync(processorId, ct);

        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
            => _inner.AbandonRetryAsync(claim, ct);
    }

    // ── ControlLoop: sequential mode ──────────────────────────────────────────────

    /// <summary>
    /// A sequential loop whose handler ignores cancellation must still stop within the drain
    /// timeout instead of pinning the host until it is killed.
    /// </summary>
    [Fact]
    public async Task Sequential_StopAsync_ReturnsWithinDrainTimeout_WhenHandlerIgnoresCancellation()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync(
            [CreateEvent("stuck")],
            cancellationToken: TestContext.Current.CancellationToken);

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "drain-sequential",
            handler: async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await release.Task; // ignores the cancellation token entirely
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await handlerEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None);
            sw.Stop();

            // Pre-fix this await never returned.
            Assert.True(
                sw.Elapsed < StopBudget,
                $"StopAsync took {sw.Elapsed} — expected it to be bounded by the {DrainTimeout} drain timeout.");

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult(); // let the abandoned handler finish so nothing outlives the test
        }
    }

    /// <summary>
    /// Abandoning a stuck handler must not cost an event: the checkpoint stays at its
    /// pre-dispatch value so the event is re-delivered after a restart.
    /// </summary>
    [Fact]
    public async Task Sequential_AbandonedHandler_DoesNotAdvanceCheckpoint()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync(
            [CreateEvent("stuck")],
            cancellationToken: TestContext.Current.CancellationToken);

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "drain-sequential-checkpoint",
            handler: async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await release.Task;
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await handlerEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None);

            var checkpoint = await checkpoints.GetAsync(
                "drain-sequential-checkpoint", TestContext.Current.CancellationToken);

            // The single event at position 1 never completed, so nothing may be checkpointed.
            Assert.True(
                checkpoint is null or 0L,
                $"Checkpoint advanced to {checkpoint} even though the handler never completed.");

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ── ControlLoop: pipelined mode ───────────────────────────────────────────────

    /// <summary>
    /// The pipelined worker drain is bounded too, and the final checkpoint flush that follows
    /// it must not advance past the position whose worker was abandoned.
    /// </summary>
    [Fact]
    public async Task Pipelined_StuckWorker_IsAbandonedAndCheckpointStaysBehindIt()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        // Appended in two phases so the test never races the loop's own progress: phase one is
        // drained to a confirmed checkpoint of 1 before the stuck event even exists.
        await backend.AppendAsync([CreateEvent("fast-1")],
            cancellationToken: TestContext.Current.CancellationToken);   // position 1

        var handlerEnteredForPos2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "drain-pipelined",
            handler: async (evt, _) =>
            {
                if (evt.GlobalPosition != 2) return;

                handlerEnteredForPos2.TrySetResult();
                await release.Task; // ignores the cancellation token entirely
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions
            {
                BatchingMode = ProcessorBatchingMode.Required,
                MaxConcurrency = 3,
            },
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            // Phase one: position 1 is processed and its checkpoint durably flushed.
            await WaitForCheckpointAsync(
                checkpoints, "drain-pipelined", 1L, TestContext.Current.CancellationToken);

            // Phase two: a stuck event followed by a fast one.
            await backend.AppendAsync([CreateEvent("stuck-2")],
                cancellationToken: TestContext.Current.CancellationToken);   // position 2
            await backend.AppendAsync([CreateEvent("fast-3")],
                cancellationToken: TestContext.Current.CancellationToken);   // position 3

            await handlerEnteredForPos2.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(
                sw.Elapsed < StopBudget,
                $"StopAsync took {sw.Elapsed} — the pipelined worker drain is not bounded.");

            var checkpoint = await checkpoints.GetAsync(
                "drain-pipelined", TestContext.Current.CancellationToken);

            // Position 2's worker was abandoned mid-handler, so it never called MarkCompleted.
            // SafeCheckpoint is therefore pinned at 1 — even if position 3 completed, and even
            // though the final flush runs with CancellationToken.None.
            Assert.Equal(1L, checkpoint);

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// <c>DisposeAsync</c> must stay safe (and idempotent) after a drain timeout, even though the
    /// abandoned workers still hold tokens from the loop's <see cref="CancellationTokenSource"/>.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AfterAbandonedDrain_DoesNotThrowAndIsIdempotent()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("stuck")],
            cancellationToken: TestContext.Current.CancellationToken);

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "drain-dispose",
            handler: async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await release.Task;
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await handlerEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await cts.CancelAsync();

            await loop.DisposeAsync();
            await loop.DisposeAsync(); // second call must be a no-op, not a double teardown

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ── EventStoreHead ───────────────────────────────────────────────────────────

    /// <summary>
    /// A head refresh stuck in a backend that ignores cancellation must not block shutdown.
    /// </summary>
    [Fact]
    public async Task EventStoreHead_StopAsync_ReturnsWithinDrainTimeout_WhenBackendIgnoresCancellation()
    {
        var backend = new StallingHeadBackend();
        var head = new EventStoreHead(
            backend,
            refreshInterval: TimeSpan.FromMilliseconds(10),
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);

            await backend.WhenStalled.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await head.StopAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(
                sw.Elapsed < StopBudget,
                $"EventStoreHead.StopAsync took {sw.Elapsed} — expected the drain timeout to bound it.");
        }
        finally
        {
            backend.Release();
        }
    }

    // ── DeadLetterRetryLoop ──────────────────────────────────────────────────────

    /// <summary>
    /// The dead-letter retry loop is bounded on the same timeout. Abandoning it is safe:
    /// claimed entries are held by a time-bounded lease, so another worker picks them up once
    /// that lease expires.
    /// </summary>
    [Fact]
    public async Task DeadLetterRetryLoop_StopAsync_ReturnsWithinDrainTimeout_WhenStoreIgnoresCancellation()
    {
        var store = new StallingDeadLetterStore();
        var processor = new DelegatingProcessor(
            processorId: "drain-dead-letter",
            handler: (_, _) => Task.CompletedTask);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await loop.StartAsync(cts.Token);

            await store.WhenStalled.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(
                sw.Elapsed < StopBudget,
                $"DeadLetterRetryLoop.StopAsync took {sw.Elapsed} — expected the drain timeout to bound it.");
        }
        finally
        {
            store.Release();
        }
    }

    // ── Options plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The option is a real knob: it has a sane default and the overrides mirror carries it.
    /// </summary>
    [Fact]
    public void DrainTimeout_HasDefault_AndIsCarriedByOverrides()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ControlLoopOptions.Default.DrainTimeout);

        var overridden = new ControlLoopOverrides { DrainTimeout = TimeSpan.FromSeconds(30) }
            .ApplyTo(ControlLoopOptions.Default);

        Assert.Equal(TimeSpan.FromSeconds(30), overridden.DrainTimeout);

        // An unset override leaves the incoming value alone.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            new ControlLoopOverrides().ApplyTo(overridden).DrainTimeout);
    }
}
