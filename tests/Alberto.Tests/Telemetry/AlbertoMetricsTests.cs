using System.Diagnostics.Metrics;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Telemetry;

/// <summary>
/// Tests that freeze the metric contract: instrument units and tag sets.
/// Renaming an instrument, changing its unit, or altering its tag keys after v1
/// is a breaking change for any user who has built a dashboard or alert on them.
/// </summary>
public sealed class AlbertoMetricsTests
{
    // ── (a) Duration instruments must record seconds ──────────────────────────

    [Fact]
    public void AppendDuration_unit_is_seconds()
    {
        // OTel semantic conventions (UCUM) mandate "s" for durations.
        // The old unit was "ms"; this test ensures the v1 contract is correct
        // before anyone builds a Prometheus recording rule on it.
        AlbertoMetrics.AppendDuration.Unit.Should().Be("s",
            "OTel semantic conventions require duration units in seconds");
    }

    [Fact]
    public void ProcessingDuration_unit_is_seconds()
    {
        AlbertoMetrics.ProcessingDuration.Unit.Should().Be("s",
            "OTel semantic conventions require duration units in seconds");
    }

    [Fact]
    public async Task TelemetryConsumeMiddleware_ProcessingDuration_records_seconds_not_milliseconds()
    {
        // If ElapsedMilliseconds were still used, a 50 ms operation would produce a
        // value around 50.0 here. TotalSeconds should produce a value < 1.0 for any
        // fast in-process call, which is incompatible with ~50 in the ms domain.
        var context = MakeConsumeContext("proc", "mod");
        var middleware = TelemetryConsumeMiddleware.Create();

        var (value, _) = await CaptureFirstHistogram<double>(
            "alberto.processing.duration",
            context.ProcessorId,
            () => middleware(context, () => Task.CompletedTask));

        value.Should().BeLessThan(1.0,
            "a same-process handler completes in microseconds; a value >= 1 means milliseconds were recorded instead of seconds");
    }

    [Fact]
    public async Task TelemetryBatchConsumeMiddleware_ProcessingDuration_records_seconds_not_milliseconds()
    {
        var context = MakeBatchConsumeContext("proc", "mod");
        var middleware = TelemetryBatchConsumeMiddleware.Create();

        var (value, _) = await CaptureFirstHistogram<double>(
            "alberto.processing.duration",
            context.ProcessorId,
            () => middleware(context, () => Task.CompletedTask));

        value.Should().BeLessThan(1.0,
            "a same-process handler completes in microseconds; a value >= 1 means milliseconds were recorded instead of seconds");
    }

    // ── (b) ProcessorLag must not lose data when two modules share a processor name ─

    [Fact]
    public void RecordProcessorLag_two_modules_same_processorId_both_series_are_retained()
    {
        // Before the fix, _processorLagMeasurements was keyed on processorId alone.
        // Two modules that both declare a processor named "projection" would silently
        // overwrite each other, reporting lag for only the later writer.
        var processorId = $"projection-{Guid.NewGuid():N}";  // unique per test run

        AlbertoMetrics.RecordProcessorLag(processorId, "orders", lag: 7);
        AlbertoMetrics.RecordProcessorLag(processorId, "payments", lag: 42);

        var measurements = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");

        var forOrders = measurements
            .Where(m => TagValue(m.Tags, "module") == "orders"
                     && TagValue(m.Tags, "processor") == processorId)
            .ToList();

        var forPayments = measurements
            .Where(m => TagValue(m.Tags, "module") == "payments"
                     && TagValue(m.Tags, "processor") == processorId)
            .ToList();

        forOrders.Should().ContainSingle(
            "orders.projection must have its own lag series (not overwritten by payments.projection)");
        forPayments.Should().ContainSingle(
            "payments.projection must have its own lag series (not overwritten by orders.projection)");

        forOrders.Single().Value.Should().Be(7);
        forPayments.Single().Value.Should().Be(42);
    }

    // ── (c) ProcessingErrors and ProcessingDuration must carry {processor, module, [shard]} ─
    // The tag dimensions on these instruments must match EventsProcessed so dashboards can
    // compute error rate and latency using the same label join key.

    [Fact]
    public async Task TelemetryConsumeMiddleware_ProcessingDuration_carries_module_tag()
    {
        var context = MakeConsumeContext("proc", "my-module");
        var middleware = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstHistogram<double>(
            "alberto.processing.duration",
            context.ProcessorId,
            () => middleware(context, () => Task.CompletedTask));

        TagValue(tags, "module").Should().Be("my-module",
            "ProcessingDuration must carry a module tag to match EventsProcessed");
        TagValue(tags, "processor").Should().Be(context.ProcessorId);
    }

    [Fact]
    public async Task TelemetryConsumeMiddleware_ProcessingErrors_on_dead_letter_carries_module_tag()
    {
        var context = MakeConsumeContext("proc", "my-module");
        var middleware = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.processing.errors",
            context.ProcessorId,
            () => middleware(context, () =>
            {
                // Simulate the retry middleware marking this event as dead-lettered.
                context.DeadLettered = true;
                context.LastError = new InvalidOperationException("simulated dead-letter");
                return Task.CompletedTask;
            }));

        TagValue(tags, "module").Should().Be("my-module",
            "ProcessingErrors must carry a module tag to match EventsProcessed");
        TagValue(tags, "processor").Should().Be(context.ProcessorId);
        TagValue(tags, "exception.type").Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task TelemetryConsumeMiddleware_ProcessingErrors_on_thrown_exception_carries_module_tag()
    {
        var context = MakeConsumeContext("proc", "my-module");
        var middleware = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.processing.errors",
            context.ProcessorId,
            async () =>
            {
                try
                {
                    await middleware(context, () => throw new InvalidOperationException("boom"));
                }
                catch (InvalidOperationException) { /* expected: recorded, then re-thrown */ }
            });

        TagValue(tags, "module").Should().Be("my-module",
            "ProcessingErrors must carry a module tag even when the exception propagates");
        TagValue(tags, "processor").Should().Be(context.ProcessorId);
        TagValue(tags, "exception.type").Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task TelemetryBatchConsumeMiddleware_ProcessingDuration_carries_module_tag()
    {
        var context = MakeBatchConsumeContext("proc", "my-module");
        var middleware = TelemetryBatchConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstHistogram<double>(
            "alberto.processing.duration",
            context.ProcessorId,
            () => middleware(context, () => Task.CompletedTask));

        TagValue(tags, "module").Should().Be("my-module",
            "ProcessingDuration must carry a module tag to match EventsProcessed");
        TagValue(tags, "processor").Should().Be(context.ProcessorId);
    }

    [Fact]
    public async Task TelemetryBatchConsumeMiddleware_ProcessingErrors_on_dead_letter_carries_module_tag()
    {
        var context = MakeBatchConsumeContext("proc", "my-module");
        var middleware = TelemetryBatchConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.processing.errors",
            context.ProcessorId,
            () => middleware(context, () =>
            {
                context.DeadLetteredCount = 1;
                context.LastError = new InvalidOperationException("simulated dead-letter");
                return Task.CompletedTask;
            }));

        TagValue(tags, "module").Should().Be("my-module",
            "ProcessingErrors must carry a module tag to match EventsProcessed");
        TagValue(tags, "processor").Should().Be(context.ProcessorId);
        TagValue(tags, "exception.type").Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task TelemetryConsumeMiddleware_ProcessingDuration_carries_shard_tag_for_sharded_module()
    {
        // Sharded modules use a composite key "module#shard". TelemetryTags.ForModule
        // splits this into separate "module" and "shard" tags.
        var context = MakeConsumeContext("proc", "my-module#shard-1");
        var middleware = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstHistogram<double>(
            "alberto.processing.duration",
            context.ProcessorId,
            () => middleware(context, () => Task.CompletedTask));

        TagValue(tags, "module").Should().Be("my-module");
        TagValue(tags, "shard").Should().Be("shard-1");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes <paramref name="processorId"/> unique to the calling test.
    /// The Alberto meter is a process-wide static, so a <see cref="MeterListener"/> installed here
    /// receives measurements from every test running concurrently — including siblings that record
    /// the same instrument with a different tag set. The capture helpers correlate on this id so a
    /// test can only assert on the measurement it produced itself.
    /// </summary>
    private static string UniqueProcessorId(string processorId) => $"{processorId}-{Guid.NewGuid():N}";

    private static ConsumeEventContext MakeConsumeContext(string processorId, string moduleKey) =>
        new()
        {
            ProcessorId = UniqueProcessorId(processorId),
            ModuleKey = moduleKey,
            Envelope = MakeEnvelope(),
            IsRebuild = false,
        };

    private static BatchConsumeContext MakeBatchConsumeContext(string processorId, string moduleKey) =>
        new()
        {
            ProcessorId = UniqueProcessorId(processorId),
            ModuleKey = moduleKey,
            Envelopes = [MakeEnvelope()],
            IsRebuild = false,
        };

    private static IEventEnvelope MakeEnvelope() =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            GlobalPosition = 1,
            EventType = new EventType("test-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// Subscribes to the named instrument on the Alberto meter, fires
    /// <paramref name="action"/>, and returns the captured measurement tagged with
    /// <paramref name="processorId"/> — see <see cref="UniqueProcessorId"/> for why the correlation
    /// is required rather than simply taking the first measurement observed.
    /// Throws if no such measurement was recorded.
    /// </summary>
    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstHistogram<T>(
        string instrumentName,
        string processorId,
        Func<Task> action) where T : struct
    {
        (T Value, KeyValuePair<string, object?>[] Tags)? captured = null;
        var gate = new object();
        using var listener = BuildListener<T>(instrumentName, (v, tags) =>
        {
            if (TagValue(tags, "processor") != processorId)
                return;

            lock (gate) captured ??= (v, tags);
        });
        listener.Start();

        await action();

        lock (gate)
        {
            captured.Should().NotBeNull(
                $"expected at least one measurement on {instrumentName} tagged with processor '{processorId}' but none were recorded");
            return captured!.Value;
        }
    }

    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstCounter<T>(
        string instrumentName,
        string processorId,
        Func<Task> action) where T : struct
        => await CaptureFirstHistogram<T>(instrumentName, processorId, action);

    private static List<(T Value, KeyValuePair<string, object?>[] Tags)> CollectObservableGaugeMeasurements<T>(
        string instrumentName) where T : struct
    {
        var measurements = new List<(T, KeyValuePair<string, object?>[])>();
        using var listener = BuildListener<T>(instrumentName, (v, tags) => measurements.Add((v, tags)));
        listener.Start();
        listener.RecordObservableInstruments();
        return measurements;
    }

    /// <summary>
    /// Builds a MeterListener that fires <paramref name="onMeasurement"/> for every
    /// measurement on the named instrument in the Alberto meter.
    /// </summary>
    private static MeterListener BuildListener<T>(
        string instrumentName,
        Action<T, KeyValuePair<string, object?>[]> onMeasurement) where T : struct
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlbertoMetrics.Name && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        // Tags arrive as a ReadOnlySpan; copy to an array before the span is freed.
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
            onMeasurement(value, tags.ToArray()));
        return listener;
    }

    private static string? TagValue(KeyValuePair<string, object?>[] tags, string key) =>
        tags.FirstOrDefault(t => t.Key == key).Value?.ToString();
}
