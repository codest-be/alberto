using System.Text.Json;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Where a processor with no checkpoint starts: the whole log, the head, or where the
/// processors it replaces stopped. A checkpoint that exists always wins.
/// </summary>
public sealed class ProcessorStartTests
{
    private const string EventTypeId = "processor-start-test-event";

    [EventType(EventTypeId)]
    private sealed record Item(string Value) : IEvent;

    private sealed class Recorder(string processorId) : IBatchableProcessor
    {
        private readonly Lock _lock = new();
        private readonly List<string> _handled = [];

        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public IReadOnlyList<string> Handled
        {
            get { lock (_lock) return [.. _handled]; }
        }

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) =>
            ProcessBatchAsync([@event], cancellationToken);

        public Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
        {
            lock (_lock)
                _handled.AddRange(events.Select(e => JsonSerializer.Deserialize<Item>(e.EventData)!.Value));
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public InMemoryEventStoreBackend Backend { get; } = new();
        public InMemoryFencedCheckpointStore Checkpoints { get; } = new();
        public EventStoreHead Head { get; }
        private readonly List<ControlLoop> _loops = [];
        private bool _headStarted;

        public Rig() => Head = new EventStoreHead(Backend, TimeSpan.FromMilliseconds(10));

        public Task AppendAsync(params string[] values) =>
            Backend.AppendAsync(
                [.. values.Select(v => new EventToPersist
                {
                    EventType = new EventType(EventTypeId),
                    Tags = [],
                    EventData = JsonSerializer.Serialize(new Item(v)),
                })],
                cancellationToken: TestContext.Current.CancellationToken);

        public async Task StartHeadAsync()
        {
            await Head.StartAsync(TestContext.Current.CancellationToken);
            _headStarted = true;
        }

        public async Task<Recorder> StartAsync(string processorId, ProcessorStart start, int maxConcurrency = 1)
        {
            var recorder = new Recorder(processorId);
            var loop = new ControlLoop(recorder, Head, Backend, Checkpoints,
                TimeSpan.FromMilliseconds(10), 100,
                executionOptions: ProcessorExecutionOptions.Default with
                {
                    StartFrom = start,
                    MaxConcurrency = maxConcurrency,
                });
            _loops.Add(loop);
            await loop.StartAsync(TestContext.Current.CancellationToken);
            return recorder;
        }

        public async Task<long?> CheckpointAsync(string processorId) =>
            await Checkpoints.GetAsync(processorId, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            foreach (var loop in _loops)
                await loop.StopAsync(CancellationToken.None);
            if (_headStarted)
                await Head.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Condition never held: {because}");
    }

    private static async Task SettleAsync() => await Task.Delay(150, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Head_skips_the_history_and_handles_everything_appended_after_it(int maxConcurrency)
    {
        await using var rig = new Rig();
        await rig.AppendAsync("old-1", "old-2", "old-3");
        await rig.StartHeadAsync();

        var reactor = await rig.StartAsync("mails", ProcessorStart.Head, maxConcurrency);
        await WaitUntilAsync(() => rig.CheckpointAsync("mails").Result is not null, "the start is seeded");
        Assert.Equal(3, await rig.CheckpointAsync("mails"));

        await rig.AppendAsync("new-1", "new-2");
        await WaitUntilAsync(() => reactor.Handled.Count >= 2, "the new events are handled");
        await SettleAsync();

        Assert.Equivalent(new[] { "new-1", "new-2" }, reactor.Handled, strict: true);
    }

    [Fact]
    public async Task Head_waits_for_a_head_that_has_read_the_log_instead_of_starting_at_zero()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("old-1", "old-2");

        var reactor = await rig.StartAsync("mails", ProcessorStart.Head);
        await SettleAsync();
        // a cold head reads 0, which would replay the log
        Assert.Null((await rig.CheckpointAsync("mails")));

        await rig.StartHeadAsync();
        await WaitUntilAsync(() => rig.CheckpointAsync("mails").Result is not null, "the start is seeded");
        Assert.Equal(2, await rig.CheckpointAsync("mails"));

        await rig.AppendAsync("new-1");
        await WaitUntilAsync(() => reactor.Handled.Count >= 1, "the new event is handled");
        await SettleAsync();
        Assert.Equal(["new-1"], reactor.Handled);
    }

    [Fact]
    public async Task CheckpointOf_continues_where_the_old_processor_stopped_with_no_gap_and_no_repeat()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("e1", "e2", "e3", "e4");
        await rig.Checkpoints.SaveAsync("mails", 2, TestContext.Current.CancellationToken);
        await rig.StartHeadAsync();

        var reactor = await rig.StartAsync("mails-v2", ProcessorStart.CheckpointOf("mails"));
        await rig.AppendAsync("e5");

        await WaitUntilAsync(() => reactor.Handled.Count >= 3, "e3 to e5 are handled");
        await SettleAsync();
        Assert.Equal(["e3", "e4", "e5"], reactor.Handled);
        // the old row is left for a rollback
        Assert.Equal(2, await rig.CheckpointAsync("mails"));
    }

    [Fact]
    public async Task CheckpointOf_several_processors_starts_at_the_lowest_so_none_leaves_a_gap()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("e1", "e2", "e3", "e4");
        await rig.Checkpoints.SaveAsync("signed", 3, TestContext.Current.CancellationToken);
        await rig.Checkpoints.SaveAsync("revised", 1, TestContext.Current.CancellationToken);
        await rig.StartHeadAsync();

        var reactor = await rig.StartAsync("merged", ProcessorStart.CheckpointOf("signed", "revised"));

        await WaitUntilAsync(() => reactor.Handled.Count >= 3, "e2 to e4 are handled");
        await SettleAsync();
        Assert.Equal(["e2", "e3", "e4"], reactor.Handled);
    }

    [Fact]
    public async Task CheckpointOf_a_processor_with_no_checkpoint_starts_at_the_beginning()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("e1", "e2");
        await rig.StartHeadAsync();

        var reactor = await rig.StartAsync("mails-v2", ProcessorStart.CheckpointOf("never-ran"));

        await WaitUntilAsync(() => reactor.Handled.Count >= 2, "the whole log is handled");
        Assert.Equal(["e1", "e2"], reactor.Handled);
    }

    [Fact]
    public async Task An_existing_checkpoint_wins_over_the_start_option()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("e1", "e2", "e3");
        await rig.Checkpoints.SaveAsync("mails", 1, TestContext.Current.CancellationToken);
        await rig.StartHeadAsync();

        var reactor = await rig.StartAsync("mails", ProcessorStart.Head);

        await WaitUntilAsync(() => reactor.Handled.Count >= 2, "e2 and e3 are handled");
        await SettleAsync();
        Assert.Equal(["e2", "e3"], reactor.Handled);
    }

    [Fact]
    public async Task A_rebuild_shadow_loop_replays_the_log_even_when_its_processor_starts_at_the_head()
    {
        await using var rig = new Rig();
        await rig.AppendAsync("e1", "e2");
        await rig.StartHeadAsync();

        var shadow = await rig.StartAsync("summary::rebuild::2", ProcessorStart.Head);

        await WaitUntilAsync(() => shadow.Handled.Count >= 2, "the shadow replays everything");
        Assert.Equal(["e1", "e2"], shadow.Handled);
    }

    [Fact]
    public void CheckpointOf_needs_at_least_one_id()
    {
        var act = () => ProcessorStart.CheckpointOf();

        Assert.ThrowsAny<ArgumentException>(act);
    }
}
