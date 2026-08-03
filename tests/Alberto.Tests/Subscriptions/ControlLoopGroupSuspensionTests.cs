using System.Text.Json;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for the seam a rebuild promotion uses to park a processor's live loop.
/// </summary>
/// <remarks>
/// A promotion has two writers, and it only ever stopped one of them. The shadow loop was
/// stopped before the version flip; the live loop was left running through it, and the three
/// awaits it takes to stop the shadow, commit the flip and refresh the version cache were long
/// enough — measurably so, under load — for the live loop to consume more events into the
/// version being discarded and carry its own checkpoint past the position the rebuilt version
/// reached. The handoff that follows is monotonic and cannot pull a checkpoint back, so those
/// events were never applied to the version that went on to serve reads.
/// <para>
/// What the coordinator needs from this seam is therefore narrow and absolute: while suspended,
/// the loop must not consume, and once resumed it must pick up everything it missed.
/// </para>
/// </remarks>
public sealed class ControlLoopGroupSuspensionTests
{
    [EventType("suspension-test-event")]
    private record Counted(int Number) : IEvent;

    private sealed class CountingProcessor(string processorId) : IBatchableProcessor
    {
        private int _count;

        public int ProcessedCount => Volatile.Read(ref _count);
        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string> { "suspension-test-event" };

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
        {
            Interlocked.Add(ref _count, events.Count);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_Suspended_Loop_Stops_Consuming_And_Catches_Up_When_Resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new InMemoryEventStoreBackend();
        var checkpoints = new InMemoryCheckpointStore();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(10));
        var processor = new CountingProcessor("totals");

        var loop = new ControlLoop(
            processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 100, moduleKey: "test");
        var group = new ControlLoopGroup([loop]);

        await head.StartAsync(ct);
        await group.StartAsync(ct);

        await AppendAsync(backend, 3, ct);
        await WaitUntilAsync(() => processor.ProcessedCount == 3, "the loop to catch up", ct);

        var suspension = await group.SuspendAsync("totals", ct);

        // Everything appended from here stands in for the events that used to be consumed into
        // the version a promotion was about to discard.
        await AppendAsync(backend, 5, ct);

        // Generously longer than the 10ms polling interval: a loop that was still running would
        // have taken these many times over.
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        processor.ProcessedCount.Should().Be(
            3, "a suspended loop must not consume — that is the whole point of suspending it");

        await suspension.DisposeAsync();

        await WaitUntilAsync(
            () => processor.ProcessedCount == 8,
            "the resumed loop to pick up what arrived while it was parked", ct);

        await group.StopAsync(CancellationToken.None);
        await head.StopAsync(CancellationToken.None);
        await group.DisposeAsync();
    }

    /// <summary>
    /// A processor the group does not own is not an error. The coordinator asks by id and does
    /// not know which loops live on this replica; nothing local is writing, so there is nothing
    /// to park.
    /// </summary>
    [Fact]
    public async Task Suspending_An_Unknown_Processor_Is_A_No_Op()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = new ControlLoopGroup([]);

        var suspension = await group.SuspendAsync("nobody", ct);

        await suspension.DisposeAsync();
    }

    private static async Task AppendAsync(InMemoryEventStoreBackend backend, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            await backend.AppendAsync(
                [
                    new EventToPersist
                    {
                        EventType = new EventType(
                            EventTypeAttribute.GetEventTypeId(typeof(Counted))),
                        Tags = [],
                        EventData = JsonSerializer.Serialize(new Counted(i)),
                    },
                ],
                cancellationToken: ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20, ct);
        }

        throw new TimeoutException($"Timed out after 10s waiting for {what}.");
    }
}
