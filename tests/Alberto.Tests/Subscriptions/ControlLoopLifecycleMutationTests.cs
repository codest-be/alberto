using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Targeted tests for <see cref="ControlLoop"/>'s lifecycle surface (<c>Cancel</c>,
/// <c>StartAsync</c>/<c>StopAsync</c>, <c>DisposeAsync</c>) written to kill specific Stryker
/// mutation survivors in that region. Mirrors the patterns in <see cref="ProcessorHealthTests"/>
/// and <see cref="ControlLoopDrainTimeoutTests"/> (InMemory backend, <c>WaitForAsync</c> polling,
/// a bounded drain timeout instead of wall-clock races).
/// </summary>
public sealed class ControlLoopLifecycleMutationTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly ProcessorExecutionOptions SequentialExecution =
        new() { BatchingMode = ProcessorBatchingMode.Disabled };

    [EventType("lifecycle-mutation-event")]
    private record LifecycleEvent(string Label) : IEvent;

    private static readonly string EventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(LifecycleEvent));

    private static EventToPersist CreateEvent(string label) => new()
    {
        EventType = new EventType(EventTypeId),
        Tags = [],
        EventData = JsonSerializer.Serialize(new LifecycleEvent(label)),
    };

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(10, ct);
        }
    }

    // ── test doubles ─────────────────────────────────────────────────────────────

    private sealed class DelegatingProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task> handler) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler(@event, ct);
    }

    /// <summary>A processor that also counts how many times it was asynchronously disposed.</summary>
    private sealed class DisposableProcessor(
        string processorId,
        Func<IEventEnvelope, CancellationToken, Task>? handler = null) : IEventProcessor, IAsyncDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => handler is null ? Task.CompletedTask : handler(@event, ct);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Captures every log entry so a test can assert on level and message content.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ── Cancel() ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kills the survivor at ControlLoop.cs:115 — <c>Cancel()</c>'s body removed. <c>Cancel</c>
    /// is the fence-violation path (<see cref="ControlLoopAssembler"/>) and must actually cancel
    /// the loop's token independently of <c>StopAsync</c>: a handler observing that token proves
    /// it fired.
    /// </summary>
    [Fact]
    public async Task Cancel_CancelsTheRunningLoopsToken()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("cancel-me")],
            cancellationToken: TestContext.Current.CancellationToken);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor("cancel-token", async (_, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                observedCancellation.TrySetResult();
                throw;
            }
        });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            drainTimeout: DrainTimeout);

        using var hostCts = new CancellationTokenSource();
        await head.StartAsync(hostCts.Token);
        await loop.StartAsync(hostCts.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Cancel() directly — not StopAsync/host shutdown — is what a fence violation calls.
        loop.Cancel();

        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await loop.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await head.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// <c>Cancel()</c> before <c>StartAsync</c> must not throw (guards the null-conditional and
    /// the <see cref="ObjectDisposedException"/> catch, both part of the same survivor region).
    /// </summary>
    [Fact]
    public void Cancel_BeforeStart_DoesNotThrow()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var processor = new DelegatingProcessor("cancel-before-start", (_, _) => Task.CompletedTask);

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution);

        var act = loop.Cancel;

        act.Should().NotThrow();
    }

    // ── StopAsync: drain-timeout warning log ────────────────────────────────────

    /// <summary>
    /// Kills the survivor at ControlLoop.cs:154 — the <c>LogWarning</c> call on an abandoned
    /// drain removed. A handler that ignores cancellation must produce a Warning-level log
    /// naming the drain timeout, not just a bounded return.
    /// </summary>
    [Fact]
    public async Task StopAsync_AbandonedDrain_LogsWarning()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("stuck")],
            cancellationToken: TestContext.Current.CancellationToken);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CapturingLogger<ControlLoop>();

        var processor = new DelegatingProcessor("stop-log", async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task; // ignores cancellation entirely
        });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            logger: logger,
            drainTimeout: DrainTimeout);

        try
        {
            using var cts = new CancellationTokenSource();
            await head.StartAsync(cts.Token);
            await loop.StartAsync(cts.Token);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            await loop.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            logger.Entries.Should().Contain(e =>
                e.Level == LogLevel.Warning &&
                e.Message.Contains("did not drain", StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains("stop-log"));

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ── DisposeAsync: completed loop disposes the processor exactly once ───────

    /// <summary>
    /// Kills the survivors at ControlLoop.cs:164 (the disposed-guard comparison/removal),
    /// :176 (the negated <c>IsCompleted</c> check) and :190 (the processor-disposal call
    /// removed). For a loop that drains cleanly, <c>DisposeAsync</c> must dispose the processor
    /// exactly once, synchronously, and a second call must be a true no-op.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CleanDrain_DisposesProcessorOnceAndIsIdempotent()
    {
        var backend = new InMemoryEventStoreBackend(); // no events — loop just idles and polls
        var checkpoints = new InMemoryCheckpointStore();
        var processor = new DisposableProcessor("dispose-clean");

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));
        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: SequentialExecution,
            drainTimeout: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Let the loop actually enter its polling delay before tearing it down.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await loop.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        processor.DisposeCount.Should().Be(1);

        await loop.DisposeAsync(); // second call: no-op, not a double dispose
        processor.DisposeCount.Should().Be(1);

        await head.StopAsync(CancellationToken.None);
    }

    // ── DisposeAsync: abandoned drain defers disposal ───────────────────────────

    /// <summary>
    /// Kills the survivors at ControlLoop.cs:167 (abandoned forced to <c>true</c>),
    /// :180 (negated <c>if (abandoned)</c>), :186 (the guarding <c>return</c> removed) and
    /// :201/:206/:208 in <c>ReleaseWhenLoopExitsAsync</c> (the deferred await, null-check and
    /// disposal call). When the loop is genuinely stuck, <c>DisposeAsync</c> must return without
    /// disposing the processor, and only dispose it once the abandoned loop actually exits.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AbandonedDrain_DefersProcessorDisposalUntilLoopExits()
    {
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        await backend.AppendAsync([CreateEvent("stuck")],
            cancellationToken: TestContext.Current.CancellationToken);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DisposableProcessor("dispose-abandoned", async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task; // ignores cancellation entirely
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

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await cts.CancelAsync();

            // DisposeAsync itself blocks for the drain timeout before giving up.
            await loop.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // The handler is still stuck — disposal must NOT have happened yet.
            processor.DisposeCount.Should().Be(0);

            // Give the (would-be, if broken) background continuation a window to run early.
            await Task.Delay(150, TestContext.Current.CancellationToken);
            processor.DisposeCount.Should().Be(0);

            // Now let the handler finish; the loop exits and the deferred continuation disposes
            // the processor — exactly once.
            release.TrySetResult();
            await WaitForAsync(() => processor.DisposeCount == 1, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken); // no late double-dispose
            processor.DisposeCount.Should().Be(1);

            await head.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
