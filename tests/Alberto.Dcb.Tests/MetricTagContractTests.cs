using System.Diagnostics;
using System.Diagnostics.Metrics;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Telemetry;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Pins the frozen v1 metric contract: every instrument name, unit, and tag key set.
/// Any rename — instrument name, unit, or tag key — is a breaking change for users
/// who have built dashboards or alerts on Alberto metrics. These tests make the freeze
/// enforceable: a silent rename fails here rather than quietly breaking a dashboard.
/// </summary>
/// <remarks>
/// Coverage:
/// <list type="bullet">
///   <item>All 13 instruments registered on the Alberto meter (names + units)</item>
///   <item>Tag keys for every instrument that carries tags and can be driven in-process
///   (the two tenant-lock counters are pinned in <c>TenantProcessorLockTests</c>)</item>
///   <item>No-tag assertion for instruments that carry none</item>
///   <item>Shard splitting: sharded module keys must emit module + shard separately</item>
///   <item>Unsharded module keys must NOT emit a shard tag</item>
///   <item>Span attribute keys on Alberto.Consume activities (per-event and batch)</item>
/// </list>
/// </remarks>
public sealed class MetricTagContractTests
{
    // ── 1. Instrument inventory (names + units) ──────────────────────────────

    /// <summary>
    /// Enumerates the Alberto meter after all static fields have been initialized
    /// and asserts the exact set of instrument names and units.
    /// Any change to the instrument set — adding, renaming, or removing — must be a
    /// conscious decision: additions bump the advertised surface; renames and removals break
    /// existing dashboards and alerts.
    /// </summary>
    [Fact]
    public void Meter_registers_exact_expected_instrument_set()
    {
        var found = new List<(string Name, string? Unit)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == AlbertoMetrics.Name)
                found.Add((instrument.Name, instrument.Unit));
        };
        listener.Start();

        // Touch the static class to force initialisation of every field before asserting.
        _ = AlbertoMetrics.Meter;

        found.Should().BeEquivalentTo(
            new[]
            {
                // Counters
                ("alberto.events.appended",       "events"),
                ("alberto.events.processed",      "events"),
                ("alberto.processing.errors",     "errors"),
                ("alberto.dead_letters",          "events"),
                ("alberto.retries",               "attempts"),
                ("alberto.concurrency.conflicts", "conflicts"),
                ("alberto.tenant_locks_acquired", "locks"),
                ("alberto.tenant_lock_failures",  "failures"),
                // Histograms
                ("alberto.append.duration",       "s"),
                ("alberto.processing.duration",   "s"),
                // Observable gauges
                ("alberto.processor.lag",         "events"),
                ("alberto.owned_tenant_count",    "tenants"),
                ("alberto.tenant_cooldown_count", "tenants"),
            },
            options => options.WithoutStrictOrdering(),
            "renaming an instrument or changing its unit is a breaking change for dashboards and alerts");
    }

    // ── 2. Instruments with no tags ──────────────────────────────────────────

    [Fact]
    public void EventsAppended_carries_no_tags()
    {
        var (_, tags) = CaptureFirstCounter<long>(
            "alberto.events.appended",
            () => AlbertoMetrics.EventsAppended.Add(1));

        tags.Should().BeEmpty("alberto.events.appended has no tag dimensions");
    }

    [Fact]
    public void ConcurrencyConflicts_carries_no_tags()
    {
        var (_, tags) = CaptureFirstCounter<long>(
            "alberto.concurrency.conflicts",
            () => AlbertoMetrics.ConcurrencyConflicts.Add(1));

        tags.Should().BeEmpty("alberto.concurrency.conflicts has no tag dimensions");
    }

    [Fact]
    public void AppendDuration_carries_no_tags()
    {
        var (_, tags) = CaptureFirstHistogram<double>(
            "alberto.append.duration",
            () => AlbertoMetrics.AppendDuration.Record(0.001));

        tags.Should().BeEmpty("alberto.append.duration has no tag dimensions");
    }

    // ── 3. Core consume-path instruments: {processor, module, [shard]} ───────
    //
    // dead_letters, retries, and processor.lag are emitted by the core library
    // via ProcessorTags.ForModule.  All three must carry the same set so queries
    // can join them on (processor, module).

    [Fact]
    public async Task DeadLetters_unsharded_tag_keys_are_processor_and_module()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var retry = new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = false };
        var mw = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysPermanentClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.dead_letters",
            ctx.ProcessorId,
            () => mw(ctx, () => throw new InvalidOperationException("boom")));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module"],
            "alberto.dead_letters tag contract is {{processor, module}} for an unsharded module");
    }

    [Fact]
    public async Task DeadLetters_sharded_tag_keys_are_processor_module_shard()
    {
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var retry = new RetryOptions { MaxRetries = 0, DeadLetterOnMaxRetries = false };
        var mw = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysPermanentClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.dead_letters",
            ctx.ProcessorId,
            () => mw(ctx, () => throw new InvalidOperationException("boom")));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "shard"],
            "alberto.dead_letters tag contract is {{processor, module, shard}} for a sharded module");
    }

    [Fact]
    public async Task Retries_unsharded_tag_keys_are_processor_and_module()
    {
        var callCount = 0;
        var ctx = MakeConsumeContext("projection", "orders");
        var retry = new RetryOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
        };
        var mw = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysTransientClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.retries",
            ctx.ProcessorId,
            () => mw(ctx, () =>
            {
                if (++callCount < 2) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            }));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module"],
            "alberto.retries tag contract is {{processor, module}} for an unsharded module");
    }

    [Fact]
    public async Task Retries_sharded_tag_keys_are_processor_module_shard()
    {
        var callCount = 0;
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var retry = new RetryOptions
        {
            MaxRetries = 1,
            RetryDelay = TimeSpan.Zero,
            DeadLetterOnMaxRetries = false,
        };
        var mw = ConsumeMiddlewares.RetryAndDeadLetter(
            retry, new AlwaysTransientClassifier(), deadLetterStore: null);

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.retries",
            ctx.ProcessorId,
            () => mw(ctx, () =>
            {
                if (++callCount < 2) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            }));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "shard"],
            "alberto.retries tag contract is {{processor, module, shard}} for a sharded module");
    }

    [Fact]
    public void ProcessorLag_unsharded_tag_keys_are_processor_and_module()
    {
        var processorId = $"p-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordProcessorLag(processorId, "orders", lag: 5);

        var all = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");
        var match = all.Where(m => TagValue(m.Tags, "processor") == processorId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["processor", "module"],
            "alberto.processor.lag tag contract is {{processor, module}} for an unsharded module");
    }

    [Fact]
    public void ProcessorLag_sharded_tag_keys_are_processor_module_shard()
    {
        var processorId = $"p-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordProcessorLag(processorId, "orders#eu", lag: 7);

        var all = CollectObservableGaugeMeasurements<long>("alberto.processor.lag");
        var match = all.Where(m => TagValue(m.Tags, "processor") == processorId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["processor", "module", "shard"],
            "alberto.processor.lag tag contract is {{processor, module, shard}} for a sharded module");
    }

    // ── 4. Telemetry-package instruments: {processor, module, [shard]} ───────
    //
    // events.processed and processing.duration use TelemetryTags.ForModule, which
    // delegates to ProcessorTags.ForModule.  processing.errors adds exception.type.

    [Fact]
    public async Task EventsProcessed_unsharded_tag_keys_are_processor_and_module()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.events.processed",
            ctx.ProcessorId,
            () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module"],
            "alberto.events.processed tag contract is {{processor, module}} for an unsharded module");
    }

    [Fact]
    public async Task EventsProcessed_sharded_tag_keys_are_processor_module_shard()
    {
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.events.processed",
            ctx.ProcessorId,
            () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "shard"],
            "alberto.events.processed tag contract is {{processor, module, shard}} for a sharded module");
    }

    [Fact]
    public async Task ProcessingDuration_unsharded_tag_keys_are_processor_and_module()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstHistogramAsync<double>(
            "alberto.processing.duration",
            ctx.ProcessorId,
            () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module"],
            "alberto.processing.duration tag contract is {{processor, module}} for an unsharded module");
    }

    [Fact]
    public async Task ProcessingDuration_sharded_tag_keys_are_processor_module_shard()
    {
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstHistogramAsync<double>(
            "alberto.processing.duration",
            ctx.ProcessorId,
            () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "shard"],
            "alberto.processing.duration tag contract is {{processor, module, shard}} for a sharded module");
    }

    [Fact]
    public async Task ProcessingErrors_dead_letter_tag_keys_are_processor_module_exception_type()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.processing.errors",
            ctx.ProcessorId,
            () => mw(ctx, () =>
            {
                ctx.DeadLettered = true;
                ctx.LastError = new InvalidOperationException("simulated");
                return Task.CompletedTask;
            }));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "exception.type"],
            "alberto.processing.errors tag contract is {{processor, module, exception.type}} for an unsharded module");
    }

    [Fact]
    public async Task ProcessingErrors_sharded_tag_keys_include_shard()
    {
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var mw = TelemetryConsumeMiddleware.Create();

        var (_, tags) = await CaptureFirstCounterAsync<long>(
            "alberto.processing.errors",
            ctx.ProcessorId,
            () => mw(ctx, () =>
            {
                ctx.DeadLettered = true;
                ctx.LastError = new InvalidOperationException("simulated");
                return Task.CompletedTask;
            }));

        TagKeys(tags).Should().BeEquivalentTo(["processor", "module", "shard", "exception.type"],
            "alberto.processing.errors tag contract is {{processor, module, shard, exception.type}} for a sharded module");
    }

    // ── 5. Tenant-ownership gauges: {consumer.id, module, [shard]} ───────────
    //
    // These use "consumer.id" (not "processor") because they track which consumer
    // instance holds the lease — a higher-level concept than a single processor.
    // The module tag MUST be "module" (not "module.key") so ownership metrics can
    // be joined with throughput metrics in a single Prometheus query.

    [Fact]
    public void OwnedTenantCount_unsharded_tag_keys_are_consumer_id_and_module()
    {
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordTenantOwnership(consumerId, moduleKey: "payments", ownedCount: 3, cooldownCount: 0);

        var all = CollectObservableGaugeMeasurements<int>("alberto.owned_tenant_count");
        var match = all.Where(m => TagValue(m.Tags, "consumer.id") == consumerId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["consumer.id", "module"],
            "alberto.owned_tenant_count tag contract is {{consumer.id, module}} — 'module.key' was renamed to 'module' at v1 freeze");
    }

    [Fact]
    public void OwnedTenantCount_sharded_tag_keys_include_shard()
    {
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordTenantOwnership(consumerId, moduleKey: "payments#eu", ownedCount: 2, cooldownCount: 1);

        var all = CollectObservableGaugeMeasurements<int>("alberto.owned_tenant_count");
        var match = all.Where(m => TagValue(m.Tags, "consumer.id") == consumerId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["consumer.id", "module", "shard"],
            "alberto.owned_tenant_count tag contract is {{consumer.id, module, shard}} for a sharded module");
    }

    [Fact]
    public void TenantCooldownCount_unsharded_tag_keys_are_consumer_id_and_module()
    {
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordTenantOwnership(consumerId, moduleKey: "payments", ownedCount: 0, cooldownCount: 2);

        var all = CollectObservableGaugeMeasurements<int>("alberto.tenant_cooldown_count");
        var match = all.Where(m => TagValue(m.Tags, "consumer.id") == consumerId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["consumer.id", "module"],
            "alberto.tenant_cooldown_count tag contract is {{consumer.id, module}} — 'module.key' was renamed to 'module' at v1 freeze");
    }

    [Fact]
    public void TenantCooldownCount_sharded_tag_keys_include_shard()
    {
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordTenantOwnership(consumerId, moduleKey: "payments#us", ownedCount: 1, cooldownCount: 3);

        var all = CollectObservableGaugeMeasurements<int>("alberto.tenant_cooldown_count");
        var match = all.Where(m => TagValue(m.Tags, "consumer.id") == consumerId).ToList();

        match.Should().ContainSingle();
        TagKeys(match[0].Tags).Should().BeEquivalentTo(["consumer.id", "module", "shard"],
            "alberto.tenant_cooldown_count tag contract is {{consumer.id, module, shard}} for a sharded module");
    }

    // ── 5b. Tenant-lock counters: {consumer.id} ──────────────────────────────
    //
    // alberto.tenant_locks_acquired and alberto.tenant_lock_failures have their
    // names and units pinned above, but their tag keys are pinned in
    // Subscriptions/TenantProcessorLockTests — they are emitted only from
    // PostgresTenantProcessorLock and cannot be driven in-process.
    //
    // Their contract is {consumer.id} and nothing more. They carried tenant.id
    // until v1; it was removed because a tenant id is unbounded, and every distinct
    // tag combination is a time series the SDK keeps for the life of the process.

    // ── 6. Ownership gauge module tag value is the logical name (not raw key) ─

    [Fact]
    public void OwnedTenantCount_module_tag_value_is_logical_name_not_raw_key()
    {
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        AlbertoMetrics.RecordTenantOwnership(consumerId, moduleKey: "payments#eu", ownedCount: 1, cooldownCount: 0);

        var all = CollectObservableGaugeMeasurements<int>("alberto.owned_tenant_count");
        var match = all.Where(m => TagValue(m.Tags, "consumer.id") == consumerId).Single();

        TagValue(match.Tags, "module").Should().Be("payments",
            "the module tag must carry the logical module name, not the composite shard key");
        TagValue(match.Tags, "shard").Should().Be("eu",
            "the shard suffix must appear as a separate shard tag");
    }

    // ── 7. Span attribute keys (Alberto.Consume activities) ──────────────────
    //
    // TelemetryConsumeMiddleware and TelemetryBatchConsumeMiddleware create
    // Alberto.Consume spans.  The span attribute keys must match the metric tag
    // keys so that users can join traces and metrics in a single query.
    // In particular "module" (not the old "module.key") must be used — exactly
    // as it is on every consume-path metric.

    [Fact]
    public async Task ConsumeMiddleware_unsharded_span_attribute_keys_are_processor_module_position_type_links()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var mw = TelemetryConsumeMiddleware.Create();

        var tags = await CaptureConsumeSpanTagsAsync(ctx.ProcessorId, () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(
            ["processor.id", "module", "event.position", "event.type", "trace.links.count"],
            "Alberto.Consume span attribute contract for an unsharded module must use 'module', not 'module.key'");
    }

    [Fact]
    public async Task ConsumeMiddleware_sharded_span_attribute_keys_include_shard()
    {
        var ctx = MakeConsumeContext("projection", "orders#eu");
        var mw = TelemetryConsumeMiddleware.Create();

        var tags = await CaptureConsumeSpanTagsAsync(ctx.ProcessorId, () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(
            ["processor.id", "module", "shard", "event.position", "event.type", "trace.links.count"],
            "Alberto.Consume span attribute contract for a sharded module must emit 'shard', not 'module.shard'");
    }

    [Fact]
    public async Task BatchConsumeMiddleware_unsharded_span_attribute_keys_are_processor_module_batch_position_links()
    {
        var ctx = MakeBatchConsumeContext("projection", "orders");
        var mw = TelemetryBatchConsumeMiddleware.Create();

        var tags = await CaptureConsumeSpanTagsAsync(ctx.ProcessorId, () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(
            ["processor.id", "module", "batch.size", "event.position.first", "event.position.last", "trace.links.count"],
            "Alberto.Consume batch span attribute contract for an unsharded module must use 'module', not 'module.key'");
    }

    [Fact]
    public async Task BatchConsumeMiddleware_sharded_span_attribute_keys_include_shard()
    {
        var ctx = MakeBatchConsumeContext("projection", "orders#eu");
        var mw = TelemetryBatchConsumeMiddleware.Create();

        var tags = await CaptureConsumeSpanTagsAsync(ctx.ProcessorId, () => mw(ctx, () => Task.CompletedTask));

        TagKeys(tags).Should().BeEquivalentTo(
            ["processor.id", "module", "shard", "batch.size", "event.position.first", "event.position.last", "trace.links.count"],
            "Alberto.Consume batch span attribute contract for a sharded module must emit 'shard', not 'module.shard'");
    }

    [Fact]
    public async Task ConsumeSpan_capture_ignores_activities_started_by_other_tests()
    {
        var ctx = MakeConsumeContext("projection", "orders");
        var mw = TelemetryConsumeMiddleware.Create();

        var tags = await CaptureConsumeSpanTagsAsync(ctx.ProcessorId, async () =>
        {
            // Stands in for a test running concurrently in another collection: it consumes on
            // the same process-wide ActivitySource and dead-letters, so its span carries three
            // extra exception.* attributes. The capture must not mistake it for ours.
            var foreign = MakeConsumeContext("foreign", "orders");
            await TelemetryConsumeMiddleware.Create()(foreign, () =>
            {
                foreign.DeadLettered = true;
                foreign.LastError = new InvalidOperationException("foreign dead-letter");
                return Task.CompletedTask;
            });

            await mw(ctx, () => Task.CompletedTask);
        });

        TagKeys(tags).Should().BeEquivalentTo(
            ["processor.id", "module", "event.position", "event.type", "trace.links.count"],
            "the capture must correlate to the span this test started, not to the first Alberto.Consume span the process happens to stop");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes <paramref name="processorId"/> unique to the calling test.
    /// The Alberto meter and <c>Alberto.Dcb</c> ActivitySource are process-wide statics, so a
    /// <see cref="MeterListener"/> or <see cref="ActivityListener"/> installed here observes every
    /// measurement and span the process emits — including those of tests running concurrently in
    /// other collections. Every capture helper below correlates on this id, so a test can only ever
    /// assert on telemetry it produced itself.
    /// </summary>
    private static string UniqueProcessorId(string processorId) => $"{processorId}-{Guid.NewGuid():N}";

    private static ConsumeEventContext MakeConsumeContext(string processorId, string moduleKey) =>
        new()
        {
            ProcessorId = UniqueProcessorId(processorId),
            ModuleKey = moduleKey,
            Envelope = new EventEnvelope
            {
                Id = Guid.NewGuid(),
                GlobalPosition = 1,
                EventType = new EventType("test-event"),
                Tags = [],
                EventData = "{}",
                Metadata = new Dictionary<string, string>(),
                CreatedAt = DateTime.UtcNow,
            },
            IsRebuild = false,
        };

    private static BatchConsumeContext MakeBatchConsumeContext(string processorId, string moduleKey)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid(),
            GlobalPosition = 1,
            EventType = new EventType("test-event"),
            Tags = [],
            EventData = "{}",
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };
        return new BatchConsumeContext
        {
            ProcessorId = UniqueProcessorId(processorId),
            ModuleKey = moduleKey,
            Envelopes = [envelope],
            IsRebuild = false,
        };
    }

    /// <summary>
    /// Runs <paramref name="action"/> under an <see cref="ActivityListener"/> that subscribes to
    /// <c>Alberto.Dcb</c> activities and captures the <c>Alberto.Consume</c> span whose
    /// <c>processor.id</c> is <paramref name="processorId"/>, returning its tag key-value pairs so
    /// tests can assert the key contract.
    /// </summary>
    /// <remarks>
    /// The listener is process-wide and the ActivitySource is a static shared by the whole
    /// assembly, so it also sees consume spans started by tests running concurrently in other
    /// collections — a dead-lettering test's span, for one, carries three extra
    /// <c>exception.*</c> attributes. Matching on <paramref name="processorId"/> (unique per
    /// context, see <see cref="UniqueProcessorId"/>) is what makes this capture the span this test
    /// started rather than whichever consume span the process happened to stop first.
    /// </remarks>
    private static async Task<KeyValuePair<string, object?>[]> CaptureConsumeSpanTagsAsync(
        string processorId,
        Func<Task> action)
    {
        var captured = new List<KeyValuePair<string, object?>[]>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AlbertoMetrics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (!activity.OperationName.StartsWith(AlbertoMetrics.ConsumeActivityName, StringComparison.Ordinal))
                    return;

                var tags = activity.TagObjects.ToArray();
                if (TagValue(tags, "processor.id") == processorId)
                {
                    lock (captured) captured.Add(tags);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await action();

        lock (captured)
        {
            captured.Should().ContainSingle(
                $"expected exactly one Alberto.Consume activity for processor '{processorId}' to be started and stopped");
            return captured[0];
        }
    }

    private static (T Value, KeyValuePair<string, object?>[] Tags) CaptureFirstCounter<T>(
        string instrumentName,
        Action action) where T : struct
    {
        (T, KeyValuePair<string, object?>[])? captured = null;
        using var listener = BuildListener<T>(instrumentName, (v, tags) => captured ??= (v, tags));
        listener.Start();
        action();
        captured.Should().NotBeNull($"expected a measurement on {instrumentName}");
        return captured!.Value;
    }

    /// <summary>
    /// Runs <paramref name="action"/> and captures the measurement on <paramref name="instrumentName"/>
    /// tagged with <paramref name="processorId"/>.
    /// </summary>
    /// <remarks>
    /// The Alberto meter is a process-wide static, so this listener also receives measurements from
    /// tests running concurrently in other collections — which record the same instruments with
    /// different tag sets (a sharded module key adds a <c>shard</c> tag). Correlating on the
    /// per-context-unique processor id keeps the assertion on this test's own measurement.
    /// </remarks>
    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstCounterAsync<T>(
        string instrumentName,
        string processorId,
        Func<Task> action) where T : struct =>
        await CaptureFirstMeasurementAsync<T>(instrumentName, processorId, action);

    private static (T Value, KeyValuePair<string, object?>[] Tags) CaptureFirstHistogram<T>(
        string instrumentName,
        Action action) where T : struct
    {
        (T, KeyValuePair<string, object?>[])? captured = null;
        using var listener = BuildListener<T>(instrumentName, (v, tags) => captured ??= (v, tags));
        listener.Start();
        action();
        captured.Should().NotBeNull($"expected a measurement on {instrumentName}");
        return captured!.Value;
    }

    /// <inheritdoc cref="CaptureFirstCounterAsync{T}(string, string, Func{Task})"/>
    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstHistogramAsync<T>(
        string instrumentName,
        string processorId,
        Func<Task> action) where T : struct =>
        await CaptureFirstMeasurementAsync<T>(instrumentName, processorId, action);

    private static async Task<(T Value, KeyValuePair<string, object?>[] Tags)> CaptureFirstMeasurementAsync<T>(
        string instrumentName,
        string processorId,
        Func<Task> action) where T : struct
    {
        (T, KeyValuePair<string, object?>[])? captured = null;
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
                $"expected a measurement on {instrumentName} tagged with processor '{processorId}'");
            return captured!.Value;
        }
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

    private static string[] TagKeys(KeyValuePair<string, object?>[] tags) =>
        tags.Select(t => t.Key).ToArray();

    private static string? TagValue(KeyValuePair<string, object?>[] tags, string key) =>
        tags.FirstOrDefault(t => t.Key == key).Value?.ToString();

    private sealed class AlwaysPermanentClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Permanent;
    }

    private sealed class AlwaysTransientClassifier : IErrorClassifier
    {
        public ErrorClassification Classify(Exception exception) => ErrorClassification.Transient;
    }
}
