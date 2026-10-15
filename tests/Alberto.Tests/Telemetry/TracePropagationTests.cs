using System.Diagnostics;
using Alberto.Append;
using Alberto.Diagnostics;
using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Telemetry;
using Alberto.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// The trace context written at append time survives to the consumer and across the outbox:
/// stamped as W3C keys whether or not Alberto's own span exists, read back with its flags,
/// and carried onto the relay's send span and the message handed to the transport.
/// </summary>
[Collection(OtelTracingCollection.Name)]
public sealed class TracePropagationTests
{
    private static readonly ActivitySource Foreign = new("Alberto.Tests.Foreign");
    private static readonly EventType OrderPlaced = new("order-placed");

    private static ActivityListener Listen(
        ActivitySamplingResult sampling,
        string source,
        Action<Activity>? stopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task<IReadOnlyDictionary<string, string>> AppendAndReadMetadataAsync(
        Dictionary<string, string>? eventMetadata = null)
    {
        var backend = new InMemoryEventStoreBackend();
        var intercepting = new InterceptingEventStoreBackend(backend, new AppendInterceptorPipeline([]));
        await intercepting.AppendAsync(
        [
            new EventToPersist
            {
                Id = Guid.NewGuid(),
                EventType = OrderPlaced,
                Tags = [],
                EventData = "{}",
                Metadata = eventMetadata ?? [],
            },
        ]);
        return (await backend.StreamAllAsync()).Single().Metadata;
    }

    [Fact]
    public async Task Append_stamps_the_ambient_activity_when_no_alberto_span_exists()
    {
        using var listener = Listen(ActivitySamplingResult.AllDataAndRecorded, Foreign.Name);
        using var ambient = Foreign.StartActivity("request");
        ambient!.TraceStateString = "vendor=x";

        var metadata = await AppendAndReadMetadataAsync();

        Assert.Equal($"00-{ambient.TraceId}-{ambient.SpanId}-01", metadata["traceparent"]);
        Assert.Equal("vendor=x", metadata["tracestate"]);
    }

    [Fact]
    public async Task Append_does_not_persist_baggage()
    {
        using var listener = Listen(ActivitySamplingResult.AllDataAndRecorded, Foreign.Name);
        using var ambient = Foreign.StartActivity("request");
        var previous = OpenTelemetry.Baggage.Current;
        OpenTelemetry.Baggage.Current = previous.SetBaggage("user.id", "42");
        try
        {
            var metadata = await AppendAndReadMetadataAsync();

            Assert.True(metadata.ContainsKey("traceparent"));
            Assert.False(metadata.ContainsKey("baggage"));
        }
        finally { OpenTelemetry.Baggage.Current = previous; }
    }

    [Fact]
    public async Task Append_does_not_tag_a_non_append_span_with_db_system()
    {
        using var listener = Listen(ActivitySamplingResult.AllDataAndRecorded, "Alberto");
        using var root = AlbertoMetrics.Source.StartActivity("command Foo", ActivityKind.Internal)!;
        var intercepting = new InterceptingEventStoreBackend(
            new InMemoryEventStoreBackend(), new AppendInterceptorPipeline([]), dbSystem: "postgresql");

        await intercepting.AppendAsync(
        [
            new EventToPersist { Id = Guid.NewGuid(), EventType = OrderPlaced, Tags = [], EventData = "{}", Metadata = new Dictionary<string, string>() },
        ]);

        Assert.Null(root.GetTagItem("db.system.name"));
    }

    [Fact]
    public async Task Append_does_not_stamp_when_there_is_no_activity()
    {
        var metadata = await AppendAndReadMetadataAsync(new() { ["keep"] = "me" });

        Assert.False(metadata.ContainsKey("traceparent"));
        Assert.Equal("me", metadata["keep"]);
    }

    [Fact]
    public async Task Stamped_context_round_trips_including_the_not_sampled_flag()
    {
        using var listener = Listen(ActivitySamplingResult.PropagationData, Foreign.Name);
        using var ambient = Foreign.StartActivity("request");
        ambient!.TraceStateString = "vendor=x";

        var metadata = await AppendAndReadMetadataAsync();
        var read = TraceContextPropagation.Extract(metadata);

        Assert.EndsWith("-00", metadata["traceparent"]);
        Assert.Equal(ambient.TraceId, read.TraceId);
        Assert.Equal(ambient.SpanId, read.SpanId);
        Assert.Equal(ActivityTraceFlags.None, read.TraceFlags);
        Assert.Equal("vendor=x", read.TraceState);
        Assert.True(read.IsRemote);
    }

    [Fact]
    public void Extract_falls_back_to_the_legacy_keys_of_events_already_stored()
    {
        var read = TraceContextPropagation.Extract(new Dictionary<string, string>
        {
            ["_traceId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
            ["_spanId"] = "00f067aa0ba902b7",
        });

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", read.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", read.SpanId.ToHexString());
        Assert.Equal(ActivityTraceFlags.Recorded, read.TraceFlags);
    }

    [Fact]
    public void Extract_returns_default_without_any_context()
    {
        Assert.Equal(default, TraceContextPropagation.Extract(new Dictionary<string, string>()));
    }

    [Fact]
    public void Provider_exposes_traceparent_and_tracestate()
    {
        var context = new ActivityProvider().ExtractTraceContext(new Dictionary<string, string>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00",
            ["tracestate"] = "vendor=x",
        });

        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00", context!.TraceParent);
        Assert.Equal("vendor=x", context.TraceState);
    }

    private sealed class ActivityProvider : ITraceContextProvider
    {
        public TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata) =>
            new Alberto.Telemetry.ActivityTraceContextProvider().ExtractTraceContext(metadata);
    }

    [Fact]
    public async Task Outbox_entry_carries_the_source_events_trace_context()
    {
        var registry = new MessageMappingRegistry(null);
        registry.Map<OutboxPropagationEvent>((_, _, _) => ValueTask.FromResult<ExternalMessage?>(
            new ExternalMessage("order.placed", "1", "{}", new Dictionary<string, string> { ["own"] = "header" })));
        var store = new InMemoryOutboxStore();
        var handler = new OutboxHandler(
            registry, store, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        await handler.ProcessEventAsync(Envelope(new Dictionary<string, string>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00",
            ["tracestate"] = "vendor=x",
        }), default);
        await handler.ProcessEventAsync(Envelope(new Dictionary<string, string>
        {
            ["_traceId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
            ["_spanId"] = "00f067aa0ba902b7",
        }), default);

        var entries = store.Entries;
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00", entries[0].Metadata["traceparent"]);
        Assert.Equal("vendor=x", entries[0].Metadata["tracestate"]);
        Assert.Equal("header", entries[0].Metadata["own"]);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", entries[1].Metadata["traceparent"]);
    }

    [Fact]
    public async Task Relay_send_span_is_a_child_of_the_entry_context_and_its_traceparent_reaches_the_transport()
    {
        var spans = new List<Activity>();
        using var listener = Listen(ActivitySamplingResult.AllDataAndRecorded, AlbertoMetrics.Name, spans.Add);
        var parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var entry = new OutboxEntry(
            Guid.NewGuid(), Guid.NewGuid(), "order.placed", "1", "{}",
            new Dictionary<string, string> { ["traceparent"] = parent },
            OutboxEntryStatus.Pending, 0, null, DateTimeOffset.UtcNow, null, TenantId: "t1");
        var store = new InMemoryOutboxStore();
        await store.InsertAsync(entry);
        var transport = new InMemoryTransport();

        using var relay = new OutboxRelay(store, transport, pollingInterval: TimeSpan.FromMilliseconds(10));
        await relay.StartAsync(default);
        await WaitUntil(() => store.Entries.Single().Status == OutboxEntryStatus.Delivered);
        await relay.StopAsync(default);

        Activity span;
        lock (spans) span = spans.Single(a => (string?)a.GetTagItem("messaging.message.id") == entry.Id.ToString());
        Assert.Equal("send order.placed", span.DisplayName);
        Assert.Equal(ActivityKind.Producer, span.Kind);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", span.TraceId.ToHexString());
        Assert.Equal("00f067aa0ba902b7", span.ParentSpanId.ToHexString());
        Assert.Equal("t1", span.GetTagItem("alberto.tenant.id"));
        Assert.Equal(1, span.GetTagItem("alberto.outbox.attempt"));
        Assert.Equal(true, span.GetTagItem("alberto.outbox.marked_delivered"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("send", span.GetTagItem("messaging.operation.type"));
        Assert.Equal("alberto", span.GetTagItem("messaging.system"));

        var sent = transport.Published.Single(m => m.MessageId == entry.Id);
        Assert.Equal($"00-{span.TraceId}-{span.SpanId}-01", sent.Metadata["traceparent"]);
        // The stored entry still names the append, not the attempt.
        Assert.Equal(parent, store.Entries.Single().Metadata["traceparent"]);
    }

    [Fact]
    public async Task Relay_send_span_records_error_type_without_the_message()
    {
        var spans = new List<Activity>();
        using var listener = Listen(ActivitySamplingResult.AllDataAndRecorded, AlbertoMetrics.Name, spans.Add);
        var entry = new OutboxEntry(
            Guid.NewGuid(), Guid.NewGuid(), "order.placed", "1", "{}", [],
            OutboxEntryStatus.Pending, 0, null, DateTimeOffset.UtcNow, null);
        var store = new InMemoryOutboxStore();
        await store.InsertAsync(entry);

        using var relay = new OutboxRelay(
            store, new ThrowingTransport(), pollingInterval: TimeSpan.FromMilliseconds(10), maxRetries: 1);
        await relay.StartAsync(default);
        await WaitUntil(() => store.Entries.Single().Status == OutboxEntryStatus.Failed);
        await relay.StopAsync(default);

        Activity span;
        lock (spans) span = spans.First(a => (string?)a.GetTagItem("messaging.message.id") == entry.Id.ToString());
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
    }

    private sealed class ThrowingTransport : IMessageTransport
    {
        public Task PublishAsync(ExternalMessage message, CancellationToken ct) =>
            throw new InvalidOperationException("secret SELECT * FROM x");
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    [EventType("outbox-propagation-event")]
    private sealed record OutboxPropagationEvent : IEvent;

    private static EventEnvelope Envelope(Dictionary<string, string> metadata) => new()
    {
        Id = Guid.NewGuid(),
        GlobalPosition = 1,
        EventType = new EventType("outbox-propagation-event"),
        Tags = [],
        EventData = "{}",
        Metadata = metadata,
        CreatedAt = DateTime.UtcNow,
    };
}
