using System.Diagnostics;
using System.Diagnostics.Metrics;
using Alberto.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.Telemetry;

/// <summary>
/// Every Alberto instrument and the state behind its gauges, built on one <see cref="Meter"/>.
/// </summary>
/// <remarks>
/// One instance per container, on the meter <see cref="IMeterFactory"/> hands out, so a host's
/// metrics are scoped to it and tests stop sharing gauge state through a static.
/// <see cref="AlbertoMetrics.Shared"/> is the fallback on the static meter, for components built
/// outside DI and for the public <see cref="AlbertoMetrics"/> fields. Record each measurement on
/// exactly one instance: the SDK merges same-named instruments, so recording on both counts twice.
/// </remarks>
internal sealed class AlbertoInstruments
{
    // Seconds-scale boundaries for latency histograms; the SDK default (0..10000) is built for
    // milliseconds and puts every sub-second observation in its first bucket.
    private static readonly double[] LatencyBuckets = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    // Event age spans a healthy sub-second tail up to a stalled processor, so it runs to an hour.
    private static readonly double[] AgeBuckets = [.. LatencyBuckets, 30, 60, 300, 900, 3600];

    internal AlbertoInstruments(Meter meter)
    {
        Meter = meter;
        EventsAppended = meter.CreateCounter<long>("alberto.events.appended", "events", "Number of events appended to the store");
        EventsProcessed = meter.CreateCounter<long>("alberto.events.processed", "events", "Number of events processed by consumers");
        ProcessingErrors = meter.CreateCounter<long>("alberto.processing.errors", "errors", "Number of event processing errors");
        DeadLetters = meter.CreateCounter<long>("alberto.dead_letters", "events", "Number of events moved to dead letter");
        OutboxPublished = meter.CreateCounter<long>("alberto.outbox.published", "messages", "Number of outbox messages published to the transport");
        OutboxPublishFailures = meter.CreateCounter<long>("alberto.outbox.publish.failures", "failures", "Number of failed outbox publish attempts");
        Retries = meter.CreateCounter<long>("alberto.retries", "attempts", "Number of retry attempts");
        ConcurrencyConflicts = meter.CreateCounter<long>("alberto.concurrency.conflicts", "conflicts", "Number of optimistic concurrency conflicts");
        TenantLocksAcquired = meter.CreateCounter<long>("alberto.tenant_locks_acquired", "locks", "Number of tenant locks successfully acquired");
        TenantLockFailures = meter.CreateCounter<long>("alberto.tenant_lock_failures", "failures", "Number of failed tenant lock acquisition attempts");

        ProcessorLag = meter.CreateObservableGauge("alberto.processor.lag", () => Snapshot(_processorLag), "events",
            "Number of events a processor is behind the global position");
        HeadStalled = meter.CreateObservableGauge("alberto.head.stalled", () => Snapshot(_headStalled), "s",
            "Seconds the stable-head barrier has held the head below committed events");
        OwnedTenantCount = meter.CreateObservableGauge("alberto.owned_tenant_count", () => Snapshot(_tenantOwnership), "tenants",
            "Number of tenants currently owned by this consumer");

        AppendDuration = meter.CreateHistogram<double>("alberto.append.duration", "s", "Duration of event append operations",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });
        ProcessingDuration = meter.CreateHistogram<double>("alberto.processing.duration", "s", "Duration of event processing operations",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });
        OutboxDeliveryLatency = meter.CreateHistogram<double>("alberto.outbox.delivery.latency", "s", "Time from outbox entry creation to successful publish",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });
        EventAge = meter.CreateHistogram<double>("alberto.event.age", "s", "Age of an event when a processor starts handling it",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = AgeBuckets });
    }

    /// <summary>
    /// Registers the container's instance: on the meter <see cref="IMeterFactory"/> creates when the
    /// host has one (a generic host does), else <see cref="AlbertoMetrics.Shared"/>. One per
    /// container, whatever the number of modules: the factory hands every caller the same cached
    /// meter, and two instances on it would register each observable gauge twice.
    /// </summary>
    internal static void Register(IServiceCollection services) =>
        services.TryAddSingleton(sp => sp.GetService<IMeterFactory>() is { } factory
            ? new AlbertoInstruments(factory.Create(new MeterOptions(AlbertoMetrics.Name) { Version = AlbertoMetrics.Version }))
            : AlbertoMetrics.Shared);

    /// <summary>The container's instance, or <see cref="AlbertoMetrics.Shared"/> if nothing registered one.</summary>
    internal static AlbertoInstruments For(IServiceProvider services) =>
        services.GetService<AlbertoInstruments>() ?? AlbertoMetrics.Shared;

    internal Meter Meter { get; }

    internal Counter<long> EventsAppended { get; }
    internal Counter<long> EventsProcessed { get; }
    internal Counter<long> ProcessingErrors { get; }
    internal Counter<long> DeadLetters { get; }
    internal Counter<long> OutboxPublished { get; }
    internal Counter<long> OutboxPublishFailures { get; }
    internal Counter<long> Retries { get; }
    internal Counter<long> ConcurrencyConflicts { get; }
    internal Counter<long> TenantLocksAcquired { get; }
    internal Counter<long> TenantLockFailures { get; }
    internal ObservableGauge<long> ProcessorLag { get; }
    internal ObservableGauge<double> HeadStalled { get; }
    internal ObservableGauge<int> OwnedTenantCount { get; }
    internal Histogram<double> AppendDuration { get; }
    internal Histogram<double> ProcessingDuration { get; }
    internal Histogram<double> OutboxDeliveryLatency { get; }
    internal Histogram<double> EventAge { get; }

    // Each gauge reads a dictionary keyed by whatever identifies one series, so an update is an
    // upsert and a component that goes away removes its own series instead of leaving the last
    // reading behind for the life of the process.

    // "module:processorId": two modules can each define a processor named "projection", and the
    // raw (possibly "module#shard") key keeps two shards of one module apart.
    private readonly Dictionary<string, Measurement<long>> _processorLag = new();

    // The physical module key: one EventStoreHead per module, or per shard.
    private readonly Dictionary<string, Measurement<double>> _headStalled = new();

    // (consumerId, moduleKey): one consumer id is reused across every module it consumes, so
    // keying on the consumer alone let the last module to report overwrite the others.
    private readonly Dictionary<(string, string), Measurement<int>> _tenantOwnership = new();

    private static Measurement<T>[] Snapshot<TKey, T>(Dictionary<TKey, Measurement<T>> series)
        where TKey : notnull where T : struct
    {
        lock (series) return series.Values.ToArray();
    }

    private static void Set<TKey, T>(Dictionary<TKey, Measurement<T>> series, TKey key, Measurement<T> value)
        where TKey : notnull where T : struct
    {
        lock (series) series[key] = value;
    }

    private static void Remove<TKey, T>(Dictionary<TKey, Measurement<T>> series, TKey key)
        where TKey : notnull where T : struct
    {
        lock (series) series.Remove(key);
    }

    internal void RecordProcessorLag(string processorId, string module, long lag) =>
        Set(_processorLag, $"{module}:{processorId}", new Measurement<long>(lag, ProcessorTags.ForModule(processorId, module)));

    internal void ForgetProcessorLag(string processorId, string module) =>
        Remove(_processorLag, $"{module}:{processorId}");

    internal void RecordHeadStalled(string moduleKey, double stalledSeconds) =>
        Set(_headStalled, moduleKey, new Measurement<double>(stalledSeconds, ModuleTags(new TagList(), moduleKey)));

    internal void ForgetHeadStalled(string moduleKey) => Remove(_headStalled, moduleKey);

    internal void RecordTenantOwnership(string consumerId, string moduleKey, int ownedCount) =>
        Set(_tenantOwnership, (consumerId, moduleKey),
            new Measurement<int>(ownedCount, ModuleTags(new TagList { { "consumer.id", consumerId } }, moduleKey)));

    // module carries the logical name and shard the database suffix, matching ProcessorTags.ForModule.
    private static TagList ModuleTags(TagList tags, string moduleKey)
    {
        tags.Add("module", ShardKey.ModuleOf(moduleKey));
        if (ShardKey.ShardOf(moduleKey) is { } shardId)
            tags.Add("shard", shardId);
        return tags;
    }
}
