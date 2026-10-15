using System.Diagnostics;
using System.Text.Json;
using Alberto.Append;
using Alberto.Commands;
using Alberto.InMemory;
using Alberto.Telemetry;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// One command execution is one trace: a root span around the commit, load and decide beneath
/// it, reads beneath load, and the append beneath the root.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class CommandSpanTests
{
    [EventType("span-test-created")]
    private sealed record Created : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    [EventType("span-test-confirmed")]
    private sealed record Confirmed : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    private sealed record ConfirmCommand(Guid OrderId);

    private sealed record RejectCommand(Guid OrderId);

    private sealed record ConflictCommand(Guid OrderId);

    private sealed class Harness : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _stopped = [];
        private readonly EventSerializer _serializer = EventSerializer.FromRegistry(
            new Dictionary<string, Type>
            {
                ["span-test-created"] = typeof(Created),
                ["span-test-confirmed"] = typeof(Confirmed),
            },
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        public Harness()
        {
            var backend = new InterceptingEventStoreBackend(
                new InMemoryEventStoreBackend(),
                new AppendInterceptorPipeline([new TelemetryAppendInterceptor()]),
                dbSystem: "test-db");
            Events = new EventStore(backend);
            Store = new AlbertoStore(Events, _serializer);

            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == AlbertoMetrics.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (_stopped) _stopped.Add(a); },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public EventStore Events { get; }
        public AlbertoStore Store { get; }

        public Task Append(IEvent @event) =>
            Events.AppendAsync(
                [new EventToPersist
                {
                    EventType = EventType.FromType(@event.GetType()),
                    Tags = _serializer.ExtractTags(@event),
                    EventData = _serializer.Serialize(@event),
                    Metadata = new Dictionary<string, string>(),
                }],
                cancellationToken: CancellationToken.None);

        /// <summary>The spans of the trace whose root is the command of the given type.</summary>
        public List<Activity> Trace(string commandType)
        {
            lock (_stopped)
            {
                var root = _stopped.Single(a => a.OperationName == $"alberto.command {commandType}");
                return _stopped.Where(a => a.TraceId == root.TraceId).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static DcbQuery Boundary(Guid id) => DcbQuery.ByTags(new EventTag("order", id.ToString()));

    private static int Folded(int state, IEvent _) => state + 1;

    private static Activity Single(List<Activity> trace, string name) =>
        trace.Single(a => a.OperationName == name);

    [Fact]
    public async Task A_committed_command_is_a_root_with_load_decide_and_append_beneath_it()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        await harness.Append(new Created { OrderId = id });

        var result = await harness.Store.Handle(new ConfirmCommand(id))
            .Load(Boundary(id), 0, Folded)
            .Decide((c, _) => Decision.Succeed(new Confirmed { OrderId = c.OrderId }))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var trace = harness.Trace(nameof(ConfirmCommand));
        var root = Single(trace, "alberto.command ConfirmCommand");
        var load = Single(trace, "alberto.load");
        var decide = Single(trace, "alberto.decide");
        var append = Single(trace, AlbertoMetrics.AppendActivityName);
        var read = Single(trace, "alberto.read");

        Assert.Null(root.Parent);
        Assert.Equal(ActivityKind.Internal, root.Kind);
        Assert.Equal("committed", root.GetTagItem("alberto.command.outcome"));
        Assert.Equal(1, root.GetTagItem("alberto.command.attempt"));
        Assert.Equal(root.SpanId, load.ParentSpanId);
        Assert.Equal(root.SpanId, decide.ParentSpanId);
        Assert.Equal(root.SpanId, append.ParentSpanId);
        Assert.Equal(load.SpanId, read.ParentSpanId);

        Assert.Equal(1, load.GetTagItem("alberto.load.events"));
        Assert.Equal(1L, load.GetTagItem("alberto.load.position"));
        Assert.Equal(0, load.GetTagItem("alberto.load.types"));
        Assert.Equal(1, load.GetTagItem("alberto.load.tags"));
        Assert.Equal("accepted", decide.GetTagItem("alberto.decide.outcome"));
        Assert.Equal(1, read.GetTagItem("alberto.read.events"));
        Assert.Equal("test-db", read.GetTagItem("db.system.name"));
        Assert.Equal(ActivityKind.Client, read.Kind);
    }

    [Fact]
    public async Task A_rejected_command_reports_its_problem_codes_and_does_not_append()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();

        var result = await harness.Store.Handle(new RejectCommand(id))
            .Load(Boundary(id), 0, Folded)
            .Decide((_, _) => Decision.Fail(Problem.Create("order.already_shipped", "no")))
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        var trace = harness.Trace(nameof(RejectCommand));
        var root = Single(trace, "alberto.command RejectCommand");
        var decide = Single(trace, "alberto.decide");

        Assert.Equal("rejected", root.GetTagItem("alberto.command.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
        Assert.Equal("rejected", decide.GetTagItem("alberto.decide.outcome"));
        Assert.Equal(new[] { "order.already_shipped" }, (string[])decide.GetTagItem("alberto.problem.codes")!);
        Assert.DoesNotContain(trace, a => a.OperationName == AlbertoMetrics.AppendActivityName);
    }

    [Fact]
    public async Task A_conflict_retry_tags_each_attempt_and_the_command_still_commits()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        await harness.Append(new Created { OrderId = id });
        var folds = 0;

        var result = await harness.Store.Handle(new ConflictCommand(id))
            .Load(Boundary(id), 0, (state, _) =>
            {
                // Only the first attempt races an append in.
                if (Interlocked.Increment(ref folds) == 1)
                    harness.Append(new Created { OrderId = id }).GetAwaiter().GetResult();
                return state + 1;
            })
            .Decide((c, _) => Decision.Succeed(new Confirmed { OrderId = c.OrderId }))
            .RetryOnConflict(3)
            .Commit(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var trace = harness.Trace(nameof(ConflictCommand));
        var root = Single(trace, "alberto.command ConflictCommand");
        var loads = trace.Where(a => a.OperationName == "alberto.load").OrderBy(a => a.StartTimeUtc).ToList();

        Assert.Equal("committed", root.GetTagItem("alberto.command.outcome"));
        Assert.Equal(2, root.GetTagItem("alberto.command.attempt"));
        Assert.Equal([1, 2], loads.Select(l => l.GetTagItem("alberto.command.attempt")));
        Assert.All(loads, l => Assert.Equal(root.SpanId, l.ParentSpanId));
        // The conflicting append failed, so it is a failed span under the same root.
        Assert.Contains(trace, a => a.OperationName == AlbertoMetrics.AppendActivityName
                                    && a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task An_exhausted_conflict_marks_the_root_without_leaking_the_exception_message()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        await harness.Append(new Created { OrderId = id });

        await Assert.ThrowsAsync<DcbConflictException>(() =>
            harness.Store.Handle(new ConflictCommand(id))
                .Load(Boundary(id), 0, (state, _) =>
                {
                    harness.Append(new Created { OrderId = id }).GetAwaiter().GetResult();
                    return state + 1;
                })
                .Decide((c, _) => Decision.Succeed(new Confirmed { OrderId = c.OrderId }))
                .Commit(TestContext.Current.CancellationToken));

        var root = Single(harness.Trace(nameof(ConflictCommand)), "alberto.command ConflictCommand");
        Assert.Equal("conflict", root.GetTagItem("alberto.command.outcome"));
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Null(root.StatusDescription);
        Assert.Equal(typeof(DcbConflictException).FullName, root.GetTagItem("error.type"));
    }
}
