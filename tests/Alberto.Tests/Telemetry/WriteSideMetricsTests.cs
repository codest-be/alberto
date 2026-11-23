using System.Diagnostics.Metrics;
using Alberto.Append;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// Pins the write-side metric dimensions, the histogram bucket advice and the event-age metric.
/// </summary>
/// <remarks>
/// The meter is process-wide, so every capture correlates on a module key or processor id unique
/// to the test rather than taking the first measurement that arrives.
/// </remarks>
public sealed class WriteSideMetricsTests
{
    private static readonly IReadOnlyCollection<IEventEnvelope> NoResult = [];

    private static AppendContext Context() => new()
    {
        Events = [new EventToPersist { Id = Guid.NewGuid(), EventType = new EventType("e"), Tags = [], EventData = "{}" }],
    };

    private static DcbConflictException Conflict() => new(5, 1, DcbQuery.All);

    [Fact]
    public async Task Successful_append_tags_module_and_outcome_success()
    {
        var module = $"orders-{Guid.NewGuid():N}";
        var seen = Capture(module, "alberto.append.duration", "alberto.events.appended");

        await new TelemetryAppendInterceptor(moduleKey: module)
            .OnAppendingAsync(Context(), _ => Task.FromResult(NoResult));

        Assert.Equivalent(new[] { "module", "outcome" }, Keys(seen["alberto.append.duration"]), strict: true);
        Assert.Equal("success", Value(seen["alberto.append.duration"], "outcome"));
        Assert.Equivalent(new[] { "module" }, Keys(seen["alberto.events.appended"]), strict: true);
    }

    [Fact]
    public async Task Sharded_module_adds_shard_tag()
    {
        var module = $"orders-{Guid.NewGuid():N}";
        var seen = Capture(module, "alberto.append.duration");

        await new TelemetryAppendInterceptor(moduleKey: $"{module}#eu")
            .OnAppendingAsync(Context(), _ => Task.FromResult(NoResult));

        Assert.Equivalent(new[] { "module", "shard", "outcome" }, Keys(seen["alberto.append.duration"]), strict: true);
    }

    [Fact]
    public async Task Conflict_is_counted_as_append_kind_and_outcome_conflict()
    {
        var module = $"orders-{Guid.NewGuid():N}";
        var seen = Capture(module, "alberto.append.duration", "alberto.concurrency.conflicts");

        var act = () => new TelemetryAppendInterceptor(moduleKey: module)
            .OnAppendingAsync(Context(), _ => throw Conflict());
        await Assert.ThrowsAnyAsync<DcbConflictException>(act);

        Assert.Equal("conflict", Value(seen["alberto.append.duration"], "outcome"));
        Assert.Equivalent(new[] { "module", "kind" }, Keys(seen["alberto.concurrency.conflicts"]), strict: true);
        Assert.Equal("append", Value(seen["alberto.concurrency.conflicts"], "kind"));
    }

    [Fact]
    public async Task Other_failures_have_outcome_error()
    {
        var module = $"orders-{Guid.NewGuid():N}";
        var seen = Capture(module, "alberto.append.duration");

        var act = () => new TelemetryAppendInterceptor(moduleKey: module)
            .OnAppendingAsync(Context(), _ => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<InvalidOperationException>(act);

        Assert.Equal("error", Value(seen["alberto.append.duration"], "outcome"));
    }

    [Fact]
    public async Task Consume_records_event_age_with_processor_and_module()
    {
        var processor = $"proj-{Guid.NewGuid():N}";
        var seen = CaptureBy("processor", processor, "alberto.event.age");
        var ctx = new ConsumeEventContext
        {
            ProcessorId = processor,
            ModuleKey = "orders",
            Envelope = new EventEnvelope
            {
                Id = Guid.NewGuid(),
                GlobalPosition = 1,
                EventType = new EventType("e"),
                Tags = [],
                EventData = "{}",
                Metadata = new Dictionary<string, string>(),
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            },
            IsRebuild = false,
        };

        await TelemetryConsumeMiddleware.Create()(ctx, () => Task.CompletedTask);

        Assert.Equivalent(new[] { "processor", "module" }, Keys(seen["alberto.event.age"]), strict: true);
        Assert.InRange(seen["alberto.event.age"].Value, 29, 60);
    }

    [Fact]
    public void Latency_histograms_declare_second_scale_bucket_boundaries()
    {
        // AlbertoMetrics has no static constructor, so its fields are initialised on first access,
        // not on first mention of the type. Without this the test passed only when an earlier test
        // in the run had already touched it (net11.0 runs reach it first more often).
        _ = AlbertoMetrics.Shared;
        var advice = new Dictionary<string, IReadOnlyList<double>?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, _) =>
        {
            if (i.Meter.Name == AlbertoMetrics.Name && i is Histogram<double> h)
                advice[i.Name] = h.Advice?.HistogramBucketBoundaries;
        };
        listener.Start();

        foreach (var name in new[] { "alberto.append.duration", "alberto.processing.duration", "alberto.outbox.delivery.latency" })
        {
            var boundaries = advice[name];
            Assert.NotNull(boundaries);
            Assert.Contains(0.001, boundaries);
            Assert.Contains(0.1, boundaries);
            Assert.Contains(10.0, boundaries);
        }

        var ageBoundaries = advice["alberto.event.age"];
        Assert.NotNull(ageBoundaries);
        Assert.Contains(10.0, ageBoundaries);
        Assert.Contains(3600.0, ageBoundaries);
    }

    [Fact]
    public void Meter_version_is_derived_not_hard_coded()
    {
        Assert.NotEqual("1.0.0", AlbertoMetrics.Version);
        Assert.DoesNotContain("+", AlbertoMetrics.Version);
        Assert.Equal(AlbertoMetrics.Version, AlbertoMetrics.Meter.Version);
    }

    private static Dictionary<string, (double Value, KeyValuePair<string, object?>[] Tags)> Capture(
        string module, params string[] instruments) => CaptureBy("module", module, instruments);

    // The listener is left running for the life of the test host; measurements are filtered on a
    // tag value that only this test emits, and callbacks fill the returned dictionary synchronously.
    private static Dictionary<string, (double Value, KeyValuePair<string, object?>[] Tags)> CaptureBy(
        string tag, string expected, params string[] instruments)
    {
        var seen = new Dictionary<string, (double Value, KeyValuePair<string, object?>[] Tags)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == AlbertoMetrics.Name && instruments.Contains(i.Name)) l.EnableMeasurementEvents(i);
        };
        void Record(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> t)
        {
            var arr = t.ToArray();
            if (arr.Any(x => x.Key == tag && (string?)x.Value is { } s && (s == expected || s.StartsWith(expected))))
                lock (seen) seen[i.Name] = (v, arr);
        }
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.Start();
        return seen;
    }

    private static string[] Keys((double Value, KeyValuePair<string, object?>[] Tags) m) => m.Tags.Select(t => t.Key).ToArray();
    private static string? Value((double Value, KeyValuePair<string, object?>[] Tags) m, string key) =>
        (string?)m.Tags.Single(t => t.Key == key).Value;
}
