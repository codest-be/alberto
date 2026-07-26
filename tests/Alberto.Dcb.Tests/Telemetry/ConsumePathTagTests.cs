using System.Diagnostics.Metrics;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Telemetry;

/// <summary>
/// Pins the tag shape for every consume-path instrument on a sharded module key.
/// These tests are the v1 API-freeze checkpoint for metric tag sets.  A sharded
/// module's processors run under the physical key <c>module#shard</c>; every
/// instrument must emit <c>module=module</c> and <c>shard=shard</c> as separate
/// tags so Prometheus queries can sum across shards with <c>sum by (module)</c>.
/// </summary>
/// <remarks>
/// Instrument families covered:
/// <list type="table">
///   <item><term>alberto.dead_letters</term><description>ConsumeMiddlewares and BatchConsumeMiddlewares</description></item>
///   <item><term>alberto.retries</term><description>RetryAndDeadLetterCore (via ConsumeMiddlewares)</description></item>
///   <item><term>alberto.processor.lag</term><description>AlbertoMetrics.RecordProcessorLag</description></item>
/// </list>
/// The Telemetry-package instruments (events.processed, processing.errors,
/// processing.duration) are covered by <see cref="AlbertoMetricsTests"/>.
/// </remarks>
public sealed class ConsumePathTagTests
{
    // ── alberto.dead_letters — ConsumeMiddlewares ─────────────────────────────

    [Fact]
    public async Task ConsumeMiddleware_DeadLetters_sharded_key_emits_split_module_and_shard_tags()
    {
        // A sharded module key "orders#eu" must produce module="orders", shard="eu".
        // Before the fix it produced module="orders#eu" with no shard tag, which made
        // the counter impossible to aggregate with other per-module instruments.
        var context = MakeConsumeContext("projection", "orders#eu");
        var retry = new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = false };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysPermanentClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.dead_letters",
            () => middleware(context, () => throw new InvalidOperationException("boom")));

        TagValue(tags, "module").Should().Be("orders",
            "the logical module key must be stripped of the shard suffix");
        TagValue(tags, "shard").Should().Be("eu",
            "the shard id must appear as a separate tag, not embedded in the module tag");
        TagValue(tags, "processor").Should().Be("projection");
    }

    [Fact]
    public async Task ConsumeMiddleware_DeadLetters_unsharded_key_has_no_shard_tag()
    {
        // An unsharded module must not emit a shard tag at all; a spurious empty tag
        // would pollute the cardinality of every query that groups by shard.
        var context = MakeConsumeContext("projection", "orders");
        var retry = new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = false };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysPermanentClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.dead_letters",
            () => middleware(context, () => throw new InvalidOperationException("boom")));

        TagValue(tags, "module").Should().Be("orders");
        tags.Any(t => t.Key == "shard").Should().BeFalse(
            "an unsharded module must not emit a shard tag");
    }

    // ── alberto.dead_letters — BatchConsumeMiddlewares ────────────────────────

    [Fact]
    public async Task BatchConsumeMiddleware_DeadLetters_sharded_key_emits_split_module_and_shard_tags()
    {
        // Single-event batch so the middleware dead-letters in-place rather than
        // re-throwing for the control loop to bisect.
        var context = MakeBatchConsumeContext("projection", "orders#eu");
        var retry = new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = false };
        var middleware = BatchConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysPermanentClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.dead_letters",
            () => middleware(context, () => throw new InvalidOperationException("boom")));

        TagValue(tags, "module").Should().Be("orders");
        TagValue(tags, "shard").Should().Be("eu");
        TagValue(tags, "processor").Should().Be("projection");
    }

    // ── alberto.retries — RetryAndDeadLetterCore ──────────────────────────────

    [Fact]
    public async Task RetryAndDeadLetterCore_Retries_sharded_key_emits_split_module_and_shard_tags()
    {
        // Trigger one retry (fail on attempt 1, succeed on attempt 2) and capture the
        // alberto.retries counter written by RetryAndDeadLetterCore.
        var callCount = 0;
        var context = MakeConsumeContext("projection", "orders#eu");
        var retry = new RetryOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
        };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysTransientClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.retries",
            () => middleware(context, () =>
            {
                callCount++;
                if (callCount < 2)
                    throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            }));

        TagValue(tags, "module").Should().Be("orders");
        TagValue(tags, "shard").Should().Be("eu");
        TagValue(tags, "processor").Should().Be("projection");
    }

    [Fact]
    public async Task RetryAndDeadLetterCore_Retries_unsharded_key_has_no_shard_tag()
    {
        var callCount = 0;
        var context = MakeConsumeContext("projection", "orders");
        var retry = new RetryOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
        };
        var middleware = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysTransientClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounter<long>(
            "alberto.retries",
            () => middleware(context, () =>
            {
                callCount++;
                if (callCount < 2)
                    throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            }));

        TagValue(tags, "module").Should().Be("orders");
        tags.Any(t => t.Key == "shard").Should().BeFalse(
            "an unsharded module must not emit a shard tag");
    }

    // ── alberto.processor.lag ─────────────────────────────────────────────────

    [Fact]
    public void RecordProcessorLag_sharded_key_emits_split_module_and_shard_tags()
    {
        var processorId = $"projection-{Guid.NewGuid():N}";

        AlbertoMetrics.RecordProcessorLag(processorId, "orders#eu", lag: 5);

        var measurements = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");

        var match = measurements
            .Where(m => TagValue(m.Tags, "processor") == processorId)
            .ToList();

        match.Should().ContainSingle("exactly one measurement for this processor");
        TagValue(match[0].Tags, "module").Should().Be("orders",
            "the logical module key must be stripped of the shard suffix");
        TagValue(match[0].Tags, "shard").Should().Be("eu",
            "the shard id must appear as a separate tag");
        match[0].Value.Should().Be(5);
    }

    [Fact]
    public void RecordProcessorLag_two_shards_same_processorId_each_has_own_measurement()
    {
        // Each shard's gauge entry must be keyed on the full physical key ("orders#shard:proc")
        // so the two shards do not clobber each other's measurement.  The emitted tags must
        // still split module and shard so the measurements are individually query-able.
        var processorId = $"projection-{Guid.NewGuid():N}";

        AlbertoMetrics.RecordProcessorLag(processorId, "orders#eu", lag: 10);
        AlbertoMetrics.RecordProcessorLag(processorId, "orders#us", lag: 20);

        var measurements = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");

        var forEu = measurements
            .Where(m => TagValue(m.Tags, "processor") == processorId
                     && TagValue(m.Tags, "shard") == "eu")
            .ToList();

        var forUs = measurements
            .Where(m => TagValue(m.Tags, "processor") == processorId
                     && TagValue(m.Tags, "shard") == "us")
            .ToList();

        forEu.Should().ContainSingle("orders#eu must have its own lag entry");
        forUs.Should().ContainSingle("orders#us must have its own lag entry");
        forEu[0].Value.Should().Be(10);
        forUs[0].Value.Should().Be(20);

        // Both entries carry the logical module name, not the composite key.
        TagValue(forEu[0].Tags, "module").Should().Be("orders");
        TagValue(forUs[0].Tags, "module").Should().Be("orders");
    }

    [Fact]
    public void RecordProcessorLag_unsharded_key_has_no_shard_tag()
    {
        var processorId = $"projection-{Guid.NewGuid():N}";

        AlbertoMetrics.RecordProcessorLag(processorId, "orders", lag: 3);

        var measurements = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");
        var match = measurements
            .Where(m => TagValue(m.Tags, "processor") == processorId)
            .ToList();

        match.Should().ContainSingle();
        TagValue(match[0].Tags, "module").Should().Be("orders");
        match[0].Tags.Any(t => t.Key == "shard").Should().BeFalse(
            "an unsharded module must not emit a shard tag");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ConsumeEventContext MakeConsumeContext(string processorId, string moduleKey) =>
        new()
        {
            ProcessorId = processorId,
            ModuleKey = moduleKey,
            Envelope = MakeEnvelope(),
            IsRebuild = false,
        };

    private static BatchConsumeContext MakeBatchConsumeContext(string processorId, string moduleKey) =>
        new()
        {
            ProcessorId = processorId,
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

    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstCounter<T>(
        string instrumentName,
        Func<Task> action) where T : struct
    {
        (T Value, KeyValuePair<string, object?>[] Tags)? captured = null;
        using var listener = BuildListener<T>(instrumentName, (v, tags) => captured ??= (v, tags));
        listener.Start();

        await action();

        captured.Should().NotBeNull(
            $"expected at least one measurement on {instrumentName} but none were recorded");
        return captured!.Value;
    }

    private static List<(T Value, KeyValuePair<string, object?>[] Tags)> CollectObservableGaugeMeasurements<T>(
        string instrumentName) where T : struct
    {
        var measurements = new List<(T, KeyValuePair<string, object?>[])>();
        using var listener = BuildListener<T>(instrumentName, (v, tags) => measurements.Add((v, tags)));
        listener.Start();
        listener.RecordObservableInstruments();
        return measurements;
    }

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
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
            onMeasurement(value, tags.ToArray()));
        return listener;
    }

    private static string? TagValue(KeyValuePair<string, object?>[] tags, string key) =>
        tags.FirstOrDefault(t => t.Key == key).Value?.ToString();

    private sealed class AlwaysTransientClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Transient;
    }

    private sealed class AlwaysPermanentClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Permanent;
    }
}
