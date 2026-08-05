using System.Diagnostics;
using System.Diagnostics.Metrics;
using Alberto.Tenancy;

namespace Alberto.Telemetry;

/// <summary>
/// Centralized telemetry instrumentation for Alberto.
/// Provides ActivitySource for tracing and Meter for metrics.
/// </summary>
public static class AlbertoMetrics
{
    /// <summary>
    /// The name used for all Alberto telemetry.
    /// </summary>
    public const string Name = "Alberto";

    /// <summary>
    /// The version of the telemetry instrumentation.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// ActivitySource for distributed tracing.
    /// </summary>
    public static readonly ActivitySource Source = new(Name, Version);

    /// <summary>
    /// Meter for metrics collection.
    /// </summary>
    public static readonly Meter Meter = new(Name, Version);

    #region Counters

    /// <summary>
    /// Counter for events successfully appended to the store.
    /// </summary>
    public static readonly Counter<long> EventsAppended =
        Meter.CreateCounter<long>("alberto.events.appended", "events", "Number of events appended to the store");

    /// <summary>
    /// Counter for events successfully processed by consumers.
    /// </summary>
    public static readonly Counter<long> EventsProcessed =
        Meter.CreateCounter<long>("alberto.events.processed", "events", "Number of events processed by consumers");

    /// <summary>
    /// Counter for event processing errors.
    /// </summary>
    public static readonly Counter<long> ProcessingErrors =
        Meter.CreateCounter<long>("alberto.processing.errors", "errors", "Number of event processing errors");

    /// <summary>
    /// Counter for events moved to dead letter.
    /// </summary>
    public static readonly Counter<long> DeadLetters =
        Meter.CreateCounter<long>("alberto.dead_letters", "events", "Number of events moved to dead letter");

    /// <summary>
    /// Counter for retry attempts.
    /// </summary>
    public static readonly Counter<long> Retries =
        Meter.CreateCounter<long>("alberto.retries", "attempts", "Number of retry attempts");

    /// <summary>
    /// Counter for optimistic concurrency conflicts.
    /// </summary>
    public static readonly Counter<long> ConcurrencyConflicts =
        Meter.CreateCounter<long>("alberto.concurrency.conflicts", "conflicts", "Number of optimistic concurrency conflicts");

    /// <summary>
    /// Counter for tenant locks successfully acquired.
    /// </summary>
    public static readonly Counter<long> TenantLocksAcquired =
        Meter.CreateCounter<long>("alberto.tenant_locks_acquired", "locks", "Number of tenant locks successfully acquired");

    /// <summary>
    /// Counter for failed tenant lock acquisition attempts.
    /// </summary>
    public static readonly Counter<long> TenantLockFailures =
        Meter.CreateCounter<long>("alberto.tenant_lock_failures", "failures", "Number of failed tenant lock acquisition attempts");

    #endregion

    #region Gauges

    /// <summary>
    /// Observable gauge for processor lag (distance from global position).
    /// </summary>
    public static readonly ObservableGauge<long> ProcessorLag =
        Meter.CreateObservableGauge("alberto.processor.lag", GetProcessorLagMeasurements, "events", "Number of events a processor is behind the global position");

    // Keyed by "module:processorId" for O(1) upsert — avoids the per-call Tags.ToArray()+LINQ scan
    // that the old List-based implementation required on every poll cycle.
    // The composite key is required because different modules can independently define a processor
    // named e.g. "projection"; keying on processorId alone would let them overwrite each other's
    // measurement, collapsing N processors into one series in Prometheus.
    private static readonly Dictionary<string, Measurement<long>> _processorLagMeasurements = new();
    private static readonly object _measurementsLock = new();

    private static IEnumerable<Measurement<long>> GetProcessorLagMeasurements()
    {
        lock (_measurementsLock) { return _processorLagMeasurements.Values.ToArray(); }
    }

    /// <summary>
    /// Updates processor lag measurements for observable gauge.
    /// </summary>
    public static void RecordProcessorLag(string processorId, string module, long lag)
    {
        lock (_measurementsLock)
        {
            // The dictionary key uses the raw physical key (which may be "module#shard") so that
            // two shards of the same module — "orders#shard1" and "orders#shard2" — each keep
            // their own entry rather than clobbering each other. The emitted tags, however, use
            // ProcessorTags.ForModule so the "module" dimension is the stripped logical name and
            // "shard" is a separate tag, matching every other metric on the consume path.
            _processorLagMeasurements[$"{module}:{processorId}"] =
                new Measurement<long>(lag, ProcessorTags.ForModule(processorId, module));
        }
    }

    #endregion

    #region Tenant Ownership Gauges

    /// <summary>
    /// Observable gauge for number of tenants owned by each consumer.
    /// </summary>
    public static readonly ObservableGauge<int> OwnedTenantCount =
        Meter.CreateObservableGauge("alberto.owned_tenant_count", GetOwnedTenantMeasurements, "tenants", "Number of tenants currently owned by this consumer");

    private record TenantOwnershipSnapshot(string ConsumerId, string ModuleKey, int OwnedCount);

    private static readonly List<TenantOwnershipSnapshot> _tenantOwnershipSnapshots = [];
    private static readonly object _tenantOwnershipLock = new();

    private static IEnumerable<Measurement<int>> GetOwnedTenantMeasurements()
    {
        lock (_tenantOwnershipLock)
        {
            return _tenantOwnershipSnapshots
                .Select(s => new Measurement<int>(s.OwnedCount, TenantOwnershipTags(s)))
                .ToArray();
        }
    }

    /// <summary>
    /// Builds the tag set for the tenant-ownership gauge.
    /// Uses <c>consumer.id</c> (the instance that owns the leases) and <c>module</c>
    /// (matching every other consume-path instrument so they can be joined in queries).
    /// For sharded modules the physical key is split: <c>module</c> carries the logical
    /// name and <c>shard</c> carries the database suffix, matching
    /// <see cref="ProcessorTags.ForModule"/>'s split.
    /// </summary>
    private static TagList TenantOwnershipTags(TenantOwnershipSnapshot s)
    {
        var tags = new TagList
        {
            { "consumer.id", s.ConsumerId },
            { "module", ShardKey.ModuleOf(s.ModuleKey) },
        };

        if (ShardKey.ShardOf(s.ModuleKey) is { } shardId)
            tags.Add("shard", shardId);

        return tags;
    }

    /// <summary>
    /// Updates tenant ownership metrics for the <c>alberto.owned_tenant_count</c> observable gauge.
    /// </summary>
    public static void RecordTenantOwnership(string consumerId, string moduleKey, int ownedCount)
    {
        lock (_tenantOwnershipLock)
        {
            _tenantOwnershipSnapshots.RemoveAll(s => s.ConsumerId == consumerId);
            _tenantOwnershipSnapshots.Add(new TenantOwnershipSnapshot(consumerId, moduleKey, ownedCount));
        }
    }

    #endregion

    #region Histograms

    /// <summary>
    /// Histogram for event append duration.
    /// </summary>
    /// <remarks>
    /// OpenTelemetry semantic conventions require durations in seconds (UCUM unit "s").
    /// Callers must record <c>sw.Elapsed.TotalSeconds</c>, not <c>sw.ElapsedMilliseconds</c>.
    /// The instrument name does not encode the unit; use the declared unit metadata instead.
    /// </remarks>
    public static readonly Histogram<double> AppendDuration =
        Meter.CreateHistogram<double>("alberto.append.duration", "s", "Duration of event append operations");

    /// <summary>
    /// Histogram for event processing duration.
    /// </summary>
    /// <remarks>
    /// OpenTelemetry semantic conventions require durations in seconds (UCUM unit "s").
    /// Callers must record <c>sw.Elapsed.TotalSeconds</c>, not <c>sw.ElapsedMilliseconds</c>.
    /// The instrument name does not encode the unit; use the declared unit metadata instead.
    /// </remarks>
    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("alberto.processing.duration", "s", "Duration of event processing operations");

    #endregion

    #region Activity Names

    /// <summary>
    /// Activity name for append operations.
    /// </summary>
    public const string AppendActivityName = "Alberto.Append";

    /// <summary>
    /// Activity name for consume operations.
    /// </summary>
    public const string ConsumeActivityName = "Alberto.Consume";

    /// <summary>
    /// Activity name for process operations.
    /// </summary>
    public const string ProcessActivityName = "Alberto.Process";

    #endregion
}
