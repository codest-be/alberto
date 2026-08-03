using Alberto.Diagnostics;
using Alberto.InMemory;
using Alberto.Telemetry;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// Unit tests for <see cref="TraceContextExtractor.ExtractTraceLink"/>.
/// The method is the single shared implementation (seam adapter) used by both
/// <see cref="TelemetryConsumeMiddleware"/> and
/// <see cref="TelemetryBatchConsumeMiddleware"/> to parse W3C traceparent
/// data from event envelope metadata.
/// </summary>
public sealed class TraceContextExtractorTests
{
    // A valid W3C trace-id (32 lowercase hex chars) and span-id (16 lowercase hex chars).
    private const string ValidTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string ValidSpanId = "00f067aa0ba902b7";

    private static IEventEnvelope MakeEnvelope(
        IReadOnlyDictionary<string, string>? metadata = null,
        long globalPosition = 1) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            GlobalPosition = globalPosition,
            EventType = new EventType("test-event"),
            Tags = [],
            EventData = "{}",
            Metadata = metadata ?? new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };

    [Fact]
    public void ExtractTraceLink_ReturnsNull_WhenProviderIsNull()
    {
        var link = TraceContextExtractor.ExtractTraceLink(MakeEnvelope(), provider: null);

        Assert.Null(link);
    }

    [Fact]
    public void ExtractTraceLink_ReturnsNull_WhenProviderReturnsNullContext()
    {
        var link = TraceContextExtractor.ExtractTraceLink(MakeEnvelope(), new NullReturningProvider());

        Assert.Null(link);
    }

    [Fact]
    public void ExtractTraceLink_ReturnsActivityLink_WhenTraceContextIsValid()
    {
        var provider = new FixedTraceContextProvider(ValidTraceId, ValidSpanId);

        var link = TraceContextExtractor.ExtractTraceLink(MakeEnvelope(), provider);

        Assert.NotNull(link);
        Assert.Equal(ValidTraceId, link.Value.Context.TraceId.ToHexString());
        Assert.Equal(ValidSpanId, link.Value.Context.SpanId.ToHexString());
    }

    [Fact]
    public void ExtractTraceLink_TagsTheLinkWithTheEventPosition()
    {
        // The link's only tag is what ties the consuming span back to a place in the log.
        // Without this assertion the whole ActivityTagsCollection can be emptied and every
        // other test here still passes — the link is still returned and its context is
        // still correct, it just no longer says which event it came from.
        var provider = new FixedTraceContextProvider(ValidTraceId, ValidSpanId);

        var link = TraceContextExtractor.ExtractTraceLink(
            MakeEnvelope(globalPosition: 4242), provider);

        Assert.NotNull(link);
        Assert.NotNull(link.Value.Tags);
        Assert.Contains(
            link.Value.Tags!,
            tag => tag.Key == "event.position" && Equals(tag.Value, 4242L));
    }

    [Fact]
    public void ExtractTraceLink_ReturnsNull_WhenTraceIdsAreMalformed()
    {
        // Too short — ActivityContext.TryParse will reject these.
        var provider = new FixedTraceContextProvider(
            traceId: "not-a-hex-trace-id",
            spanId: "bad");

        var link = TraceContextExtractor.ExtractTraceLink(MakeEnvelope(), provider);

        Assert.Null(link);
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    private sealed class NullReturningProvider : ITraceContextProvider
    {
        public TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata)
            => null;
    }

    private sealed class FixedTraceContextProvider(string traceId, string spanId)
        : ITraceContextProvider
    {
        public TraceContext? ExtractTraceContext(IReadOnlyDictionary<string, string> metadata)
            => new TraceContext(traceId, spanId);
    }
}
