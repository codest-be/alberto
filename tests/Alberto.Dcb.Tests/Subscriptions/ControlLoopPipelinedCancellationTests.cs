using System.Text.Json;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Regression tests for P0.1 / COR-1: a pipelined <see cref="ControlLoop"/> must not
/// advance the checkpoint past an event whose handler was cancelled mid-dispatch.
///
/// <para>
/// <b>Pre-fix behaviour (the bug):</b> <c>RunWorkerAsync</c> called
/// <c>MarkCompleted</c> inside a <c>finally</c> block. When <c>DispatchAsync</c>
/// was cancelled mid-handler, the <c>finally</c> still ran, marking the position
/// complete. <c>SafeCheckpoint</c> then advanced to <c>readPosition</c> and the
/// final <c>SaveWatermarkCheckpointAsync(watermark, CancellationToken.None)</c>
/// persisted a position beyond the never-processed event. On restart the event was
/// silently skipped — permanent data loss.
/// </para>
///
/// <para>
/// <b>Fix:</b> <c>MarkCompleted</c> is placed after the try/catch in the worker loop
/// body. The cancellation catch-arm calls <c>break</c>, which exits the loop without
/// reaching <c>MarkCompleted</c>. The in-flight position therefore remains in the
/// watermark and <c>SafeCheckpoint</c> stays behind it.
/// </para>
///
/// <para>
/// This race is triggered on every lease handoff (blue/green deploy, slow DB renewal).
/// Only pipelined mode (MaxConcurrency &gt; 1) is affected; sequential mode is correct.
/// </para>
/// </summary>
public sealed class ControlLoopPipelinedCancellationTests
{
    // ── shared event type ──────────────────────────────────────────────────────────

    [EventType("cancel-regression-event")]
    private record CancelRegressionEvent(string Label) : IEvent;

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static readonly string EventTypeId =
        EventTypeAttribute.GetEventTypeId(typeof(CancelRegressionEvent));

    private static EventToPersist CreateEvent(string label) =>
        new()
        {
            EventType = new EventType(EventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(new CancelRegressionEvent(label)),
        };

    /// <summary>
    /// Minimal <see cref="IEventProcessor"/> that delegates each event to a supplied handler.
    /// Used instead of the nested class in <see cref="ControlLoopTests"/> to keep the
    /// regression test self-contained.
    /// </summary>
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

    // ── PositionWatermark unit test ────────────────────────────────────────────────

    /// <summary>
    /// Isolates the core invariant that the fix relies on: <c>SafeCheckpoint</c> must
    /// remain behind any position that is still in-flight, even when all surrounding
    /// positions have completed.
    ///
    /// This test exercises <see cref="PositionWatermark"/> directly and serves as a
    /// pinpoint diagnostic: if it fails, the bug is in the watermark's own logic; if
    /// the integration test below fails but this one passes, the bug is in how the
    /// worker calls (or skips) <c>MarkCompleted</c>.
    /// </summary>
    [Fact]
    public void PositionWatermark_SafeCheckpoint_StaysBehindInFlightPositionAfterNeighboursComplete()
    {
        // Arrange: three events dispatched concurrently.
        var watermark = new PositionWatermark(initialPosition: 0);
        watermark.AdvanceReadPosition(3);

        watermark.MarkDispatched(1);
        watermark.MarkDispatched(2);
        watermark.MarkDispatched(3);

        // Act: complete positions 1 and 3 — position 2 is cancelled (never completed).
        watermark.MarkCompleted(1);
        watermark.MarkCompleted(3);

        // Assert: SafeCheckpoint == _inFlight[0] - 1 == 2 - 1 == 1, NOT readPosition (3).
        //
        // Pre-fix (MarkCompleted called in finally): position 2 would also be completed,
        // _inFlight becomes empty, SafeCheckpoint == readPosition == 3. The final flush
        // would persist 3, silently skipping the event at position 2 on restart.
        Assert.Equal(1L, watermark.SafeCheckpoint);
        Assert.Equal(1, watermark.InFlightCount); // position 2 still tracked as in-flight
    }

    // ── Integration test ──────────────────────────────────────────────────────────

    /// <summary>
    /// End-to-end regression for P0.1 / COR-1.
    ///
    /// <para>
    /// Three events are appended (positions 1, 2, 3). A pipelined loop with
    /// MaxConcurrency = 3 dispatches all three concurrently:
    /// <list type="bullet">
    ///   <item>Position 1: handler completes synchronously.</item>
    ///   <item>Position 2: handler signals a gate, then blocks indefinitely on the
    ///     cancellation token.</item>
    ///   <item>Position 3: handler completes synchronously.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Once the gate fires (position 2 is verifiably in-flight), the loop is
    /// cancelled. The worker handling position 2 catches the
    /// <see cref="OperationCanceledException"/> and calls <c>break</c> — without
    /// calling <c>MarkCompleted</c>.
    /// </para>
    ///
    /// <para>
    /// After all workers drain and the final checkpoint flush runs, the persisted
    /// checkpoint must equal 1 (the last safely-completed position). Persisting 2
    /// or 3 would mean the event at position 2 is skipped permanently on restart.
    /// </para>
    ///
    /// <b>Determinism guarantee:</b> no wall-clock <c>Task.Delay</c> races.
    /// Cancellation is triggered only after <c>handlerEnteredForPos2</c> fires,
    /// which is a <see cref="TaskCompletionSource"/> set from inside the handler.
    /// All synchronisation points are event-driven.
    /// </summary>
    [Fact]
    public async Task Pipelined_CancelledMidDispatch_CheckpointDoesNotAdvancePastCancelledEvent()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();

        // Three events; InMemory assigns sequential positions starting at 1.
        await backend.AppendAsync(
            [CreateEvent("fast-1")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 1
        await backend.AppendAsync(
            [CreateEvent("slow-cancelled")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 2
        await backend.AppendAsync(
            [CreateEvent("fast-3")],
            cancellationToken: TestContext.Current.CancellationToken);    // position 3

        // RunContinuationsAsynchronously: the continuation (our test awaiting the task)
        // is queued rather than run inline. This prevents a deadlock where the worker
        // thread would re-enter the PositionWatermark lock while already holding it.
        var handlerEnteredForPos2 = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new DelegatingProcessor(
            processorId: "pipeline-cancel-regression",
            handler: (evt, ct) =>
            {
                if (evt.GlobalPosition == 2)
                {
                    // Signal that we are actively inside the handler for position 2.
                    // This is the precise sync point: the event is in-flight and the
                    // CancellationToken has not yet been fired.
                    handlerEnteredForPos2.TrySetResult();

                    // Block until the cancellation token fires.  When ct is cancelled,
                    // Task.Delay throws OperationCanceledException, which propagates back
                    // through DispatchAsync to the worker's try/catch.
                    return Task.Delay(Timeout.Infinite, ct);
                }

                // Positions 1 and 3: complete synchronously.
                return Task.CompletedTask;
            });

        var head = new EventStoreHead(backend, refreshInterval: TimeSpan.FromMilliseconds(10));

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10),
            batchSize: 100,
            executionOptions: new ProcessorExecutionOptions(
                BatchingMode: ProcessorBatchingMode.Required,
                MaxConcurrency: 3));

        // ── Act ───────────────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource();
        await head.StartAsync(cts.Token);
        await loop.StartAsync(cts.Token);

        // Wait until the handler for position 2 is verifiably running inside a worker.
        // This is not a wall-clock race: we only proceed once the gate fires, and the
        // gate is set synchronously from within the handler itself.
        await handlerEnteredForPos2.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // Cancel: this is the "mid-dispatch cancellation" from COR-1.
        // The worker currently executing position 2's handler will receive the OCE.
        await cts.CancelAsync();
        await loop.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);

        // ── Assert ────────────────────────────────────────────────────────────────
        var checkpoint = await checkpoints.GetAsync(
            "pipeline-cancel-regression",
            TestContext.Current.CancellationToken);

        // By the time StopAsync returns, the loop's finally block has run:
        //   channel.Writer.Complete()
        //   await Task.WhenAll(workers)        ← all workers exited, MarkCompleted(1) called
        //   SaveWatermarkCheckpointAsync(...)  ← final flush with CancellationToken.None
        //
        // With the fix:
        //   _inFlight = [2] (position 2 was never MarkCompleted'd)
        //   SafeCheckpoint = _inFlight[0] - 1 = 2 - 1 = 1
        //   Final flush persists 1  ← event at position 2 will be re-processed after restart
        //
        // Without the fix (MarkCompleted in a finally block):
        //   _inFlight = [] (cancelled event still marked complete)
        //   SafeCheckpoint = readPosition = 3
        //   Final flush persists 3  ← event at position 2 is silently skipped forever
        Assert.Equal(1L, checkpoint);
    }
}
