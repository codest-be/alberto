using System.Diagnostics;
using System.Text.Json;
using Alberto.Append;
using Alberto.Commands;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Alberto.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// The proof of done for the tracing work: one inbound request is one trace from the command
/// through the append, a reactor and the outbox to the message handed to the transport.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class EndToEndTraceTests
{
    private const string HttpName = "Alberto.Tests.EndToEnd.Http";
    private static readonly ActivitySource Http = new(HttpName);

    [EventType("e2e-placed")]
    private sealed record Placed : IEvent
    {
        [Tag("order")] public required Guid OrderId { get; init; }
    }

    private sealed record PlaceCommand(Guid OrderId);

    [Fact]
    public async Task One_request_is_one_trace_from_command_to_the_message_on_the_transport()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == AlbertoMetrics.Name || s.Name == HttpName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (stopped) stopped.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var serializer = EventSerializer.FromRegistry(
            new Dictionary<string, Type> { ["e2e-placed"] = typeof(Placed) },
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var backend = new InMemoryEventStoreBackend();
        var events = new EventStore(new InterceptingEventStoreBackend(
            backend, new AppendInterceptorPipeline([new TelemetryAppendInterceptor()])));
        var store = new AlbertoStore(events, serializer);

        // Command: the inbound request is the root.
        var id = Guid.NewGuid();
        Activity root;
        root = Http.StartActivity("POST /orders", ActivityKind.Server)!;
        Assert.NotNull(root);
        using (root)
        {
            var result = await store.Handle(new PlaceCommand(id))
                .Load(DcbQuery.ByTags(new EventTag("order", id.ToString())), 0, (s, _) => s + 1)
                .Decide((c, _) => Decision.Succeed(new Placed { OrderId = c.OrderId }))
                .Commit(TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
        }

        // Consumers: a reactor (Parent) that feeds the outbox handler, and a projection (Link).
        var envelope = (await backend.StreamAllAsync()).Single();
        var registry = new MessageMappingRegistry(null);
        registry.Map<Placed>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(
            new ExternalMessage("order.placed", id.ToString(), "{}", new Dictionary<string, string>())));
        var outbox = new InMemoryOutboxStore();
        var handler = new OutboxHandler(
            registry, outbox, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var provider = new ActivityTraceContextProvider();

        ConsumeEventContext Context(string processor, TraceParenting parenting) => new()
        {
            ProcessorId = processor, ModuleKey = "orders", Envelope = envelope, IsRebuild = false,
            TraceParenting = parenting,
        };
        await TelemetryConsumeMiddleware.Create(provider)(
            Context("outbox", TraceParenting.Parent), () => handler.ProcessEventAsync(envelope, default));
        await TelemetryConsumeMiddleware.Create(provider)(
            Context("summary", TraceParenting.Link), () => Task.CompletedTask);

        // Relay.
        var transport = new InMemoryTransport();
        using var relay = new OutboxRelay(outbox, transport, pollingInterval: TimeSpan.FromMilliseconds(10));
        await relay.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (outbox.Entries.Single().Status != OutboxEntryStatus.Delivered)
        {
            Assert.True(DateTime.UtcNow < deadline, "relay did not deliver in time");
            await Task.Delay(10);
        }
        await relay.StopAsync(default);

        List<Activity> all;
        lock (stopped) all = stopped.ToList();
        var inTrace = all.Where(a => a.TraceId == root.TraceId).ToList();

        // Everything from the command to the relay's send shares the root's trace.
        foreach (var prefix in new[] { "alberto.command", "alberto.load", "alberto.decide", "append", "process outbox", "send" })
            Assert.Contains(inTrace, a => a.OperationName.StartsWith(prefix));

        // The reactor is a child of the append span; the projection is its own trace, linked back.
        var append = inTrace.Single(a => a.OperationName.StartsWith("append"));
        Assert.Equal(append.SpanId, inTrace.Single(a => a.OperationName == "process outbox").ParentSpanId);
        var projection = all.Single(a => a.OperationName == "process summary");
        Assert.NotEqual(root.TraceId, projection.TraceId);
        Assert.Equal(root.TraceId, projection.Links.Single().Context.TraceId);

        // The delivered message names the relay's send span as parent, in the same trace.
        var send = inTrace.Single(a => a.OperationName.StartsWith("send"));
        var sent = Assert.Single(transport.Published);
        Assert.True(ActivityContext.TryParse(sent.Metadata["traceparent"], null, out var carried));
        Assert.Equal(root.TraceId, carried.TraceId);
        Assert.Equal(send.SpanId, carried.SpanId);
    }
}
