using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Alberto.Dcb.Telemetry;

/// <summary>
/// Centralized telemetry instrumentation for Alberto.Dcb.
/// Provides ActivitySource for tracing and Meter for metrics.
/// </summary>
public static class AlbertoMetrics
{
    /// <summary>
    /// The name used for all Alberto telemetry.
    /// </summary>
    public const string Name = "Alberto.Dcb";

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

    /// <summary>
    /// Counter for events filtered out due to tenant ownership.
    /// </summary>
    public static readonly Counter<long> EventsFilteredByTenant =
        Meter.CreateCounter<long>("alberto.events_filtered_by_tenant", "events", "Number of events filtered out due to tenant ownership");

    /// <summary>
    /// Counter for tenant leases lost (not renewed in time, taken by another replica).
    /// </summary>
    public static readonly Counter<long> TenantLeasesLost =
        Meter.CreateCounter<long>("alberto.tenant_leases_lost", "leases", "Number of tenant leases lost due to failed renewal");

    #endregion

    #region Gauges

    /// <summary>
    /// Observable gauge for processor lag (distance from global position).
    /// </summary>
    public static readonly ObservableGauge<long> ProcessorLag =
        Meter.CreateObservableGauge("alberto.processor.lag", GetProcessorLagMeasurements, "events", "Number of events a processor is behind the global position");

    // Keyed by processorId for O(1) upsert — avoids the per-call Tags.ToArray()+LINQ scan
    // that the old List-based implementation required on every poll cycle.
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
            _processorLagMeasurements[processorId] = new Measurement<long>(lag,
                new KeyValuePair<string, object?>("processor", processorId),
                new KeyValuePair<string, object?>("module", module));
        }
    }

    #endregion

    #region Tenant Ownership Gauges

    /// <summary>
    /// Observable gauge for number of tenants owned by each consumer.
    /// </summary>
    public static readonly ObservableGauge<int> OwnedTenantCount =
        Meter.CreateObservableGauge("alberto.owned_tenant_count", GetOwnedTenantMeasurements, "tenants", "Number of tenants currently owned by this consumer");

    /// <summary>
    /// Observable gauge for number of tenants in lock cooldown.
    /// </summary>
    public static readonly ObservableGauge<int> TenantCooldownCount =
        Meter.CreateObservableGauge("alberto.tenant_cooldown_count", GetCooldownTenantMeasurements, "tenants", "Number of tenants in lock cooldown");

    private record TenantOwnershipSnapshot(string ConsumerId, string ModuleKey, int OwnedCount, int CooldownCount);

    private static readonly List<TenantOwnershipSnapshot> _tenantOwnershipSnapshots = [];
    private static readonly object _tenantOwnershipLock = new();

    private static IEnumerable<Measurement<int>> GetOwnedTenantMeasurements()
    {
        lock (_tenantOwnershipLock)
        {
            return _tenantOwnershipSnapshots.Select(s => new Measurement<int>(
                s.OwnedCount,
                new KeyValuePair<string, object?>("consumer.id", s.ConsumerId),
                new KeyValuePair<string, object?>("module.key", s.ModuleKey))).ToArray();
        }
    }

    private static IEnumerable<Measurement<int>> GetCooldownTenantMeasurements()
    {
        lock (_tenantOwnershipLock)
        {
            return _tenantOwnershipSnapshots.Select(s => new Measurement<int>(
                s.CooldownCount,
                new KeyValuePair<string, object?>("consumer.id", s.ConsumerId),
                new KeyValuePair<string, object?>("module.key", s.ModuleKey))).ToArray();
        }
    }

    /// <summary>
    /// Updates tenant ownership metrics for observable gauges.
    /// </summary>
    public static void RecordTenantOwnership(string consumerId, string moduleKey, int ownedCount, int cooldownCount)
    {
        lock (_tenantOwnershipLock)
        {
            _tenantOwnershipSnapshots.RemoveAll(s => s.ConsumerId == consumerId);
            _tenantOwnershipSnapshots.Add(new TenantOwnershipSnapshot(consumerId, moduleKey, ownedCount, cooldownCount));
        }
    }

    #endregion

    #region Histograms

    /// <summary>
    /// Histogram for event append duration.
    /// </summary>
    public static readonly Histogram<double> AppendDuration =
        Meter.CreateHistogram<double>("alberto.append.duration", "ms", "Duration of event append operations");

    /// <summary>
    /// Histogram for event processing duration.
    /// </summary>
    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("alberto.processing.duration", "ms", "Duration of event processing operations");

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
