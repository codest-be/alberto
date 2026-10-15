using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

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
    /// <remarks>
    /// The assembly's informational version with SourceLink's <c>+commit</c> suffix dropped, so a
    /// rebuild of the same release does not mint a new instrumentation scope.
    /// </remarks>
    public static readonly string Version =
        typeof(AlbertoMetrics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// ActivitySource for distributed tracing.
    /// </summary>
    public static readonly ActivitySource Source = new(Name, Version);

    /// <summary>
    /// Meter for metrics collection.
    /// </summary>
    public static readonly Meter Meter = new(Name, Version);

    /// <summary>
    /// The instruments on <see cref="Meter"/>: what every public field below reads, and what a
    /// component built outside DI records on. A container builds its own on the meter
    /// <see cref="IMeterFactory"/> hands out. Declared before the fields so their initialisers see it.
    /// </summary>
    internal static readonly AlbertoInstruments Shared = new(Meter);

    #region Counters

    /// <summary>
    /// Counter for events successfully appended to the store, tagged with <c>module</c> (and
    /// <c>shard</c> when sharded) where the module is known.
    /// </summary>
    public static readonly Counter<long> EventsAppended = Shared.EventsAppended;

    /// <summary>
    /// Counter for events successfully processed by consumers.
    /// </summary>
    public static readonly Counter<long> EventsProcessed = Shared.EventsProcessed;

    /// <summary>
    /// Counter for event processing errors.
    /// </summary>
    public static readonly Counter<long> ProcessingErrors = Shared.ProcessingErrors;

    /// <summary>
    /// Counter for events moved to dead letter.
    /// </summary>
    public static readonly Counter<long> DeadLetters = Shared.DeadLetters;

    /// <summary>
    /// Counter for outbox messages handed to the transport. Carries no tags: tenant and message
    /// type are unbounded as metric dimensions.
    /// </summary>
    public static readonly Counter<long> OutboxPublished = Shared.OutboxPublished;

    /// <summary>
    /// Counter for failed outbox publish attempts, tagged with <c>error.type</c>. A published
    /// message whose delivered-mark could not be written is not a failed publish.
    /// </summary>
    public static readonly Counter<long> OutboxPublishFailures = Shared.OutboxPublishFailures;

    /// <summary>
    /// Counter for retry attempts.
    /// </summary>
    public static readonly Counter<long> Retries = Shared.Retries;

    /// <summary>
    /// Counter for optimistic concurrency conflicts, tagged with <c>kind</c>: <c>append</c> for a
    /// <see cref="DcbConflictException"/> (plus <c>module</c>) or <c>projection</c> for a state-store
    /// write conflict.
    /// </summary>
    public static readonly Counter<long> ConcurrencyConflicts = Shared.ConcurrencyConflicts;

    /// <summary>
    /// Counter for tenant locks successfully acquired.
    /// </summary>
    public static readonly Counter<long> TenantLocksAcquired = Shared.TenantLocksAcquired;

    /// <summary>
    /// Counter for failed tenant lock acquisition attempts.
    /// </summary>
    public static readonly Counter<long> TenantLockFailures = Shared.TenantLockFailures;

    #endregion

    #region Gauges

    /// <summary>
    /// Observable gauge for processor lag (distance from global position).
    /// </summary>
    public static readonly ObservableGauge<long> ProcessorLag = Shared.ProcessorLag;

    /// <summary>
    /// Updates processor lag measurements for observable gauge.
    /// </summary>
    public static void RecordProcessorLag(string processorId, string module, long lag) =>
        Shared.RecordProcessorLag(processorId, module, lag);

    /// <summary>
    /// Observable gauge for how long <see cref="Alberto.Subscriptions.EventStoreHead"/>'s
    /// in-flight visibility barrier has held the head below committed events, in seconds.
    /// Zero both when the barrier isn't holding the head back and while a hold is still under
    /// the stall warning threshold, so an ordinary short-lived write transaction never shows up
    /// here. Any non-zero value means a write transaction has been pinning Postgres's
    /// <c>xmin</c> horizon past that threshold — see <c>idle_in_transaction_session_timeout</c>
    /// in docs/configuration.md.
    /// </summary>
    public static readonly ObservableGauge<double> HeadStalled = Shared.HeadStalled;

    /// <summary>
    /// Updates the stalled-duration measurement for the <c>alberto.head.stalled</c> gauge.
    /// Pass <c>0</c> once the barrier releases so the series returns to baseline instead of
    /// holding its last non-zero value forever.
    /// </summary>
    public static void RecordHeadStalled(string moduleKey, double stalledSeconds) =>
        Shared.RecordHeadStalled(moduleKey, stalledSeconds);

    #endregion

    #region Tenant Ownership Gauges

    /// <summary>
    /// Observable gauge for number of tenants owned by each consumer.
    /// </summary>
    public static readonly ObservableGauge<int> OwnedTenantCount = Shared.OwnedTenantCount;

    /// <summary>
    /// Updates tenant ownership metrics for the <c>alberto.owned_tenant_count</c> observable gauge.
    /// </summary>
    public static void RecordTenantOwnership(string consumerId, string moduleKey, int ownedCount) =>
        Shared.RecordTenantOwnership(consumerId, moduleKey, ownedCount);

    #endregion

    #region Histograms

    /// <summary>
    /// Histogram for event append duration, tagged with <c>outcome</c>
    /// (<c>success</c>, <c>conflict</c> or <c>error</c>) and <c>module</c>.
    /// </summary>
    /// <remarks>
    /// OpenTelemetry semantic conventions require durations in seconds (UCUM unit "s").
    /// Callers must record <c>sw.Elapsed.TotalSeconds</c>, not <c>sw.ElapsedMilliseconds</c>.
    /// The instrument name does not encode the unit; use the declared unit metadata instead.
    /// </remarks>
    public static readonly Histogram<double> AppendDuration = Shared.AppendDuration;

    /// <summary>
    /// Histogram for event processing duration.
    /// </summary>
    /// <remarks>
    /// OpenTelemetry semantic conventions require durations in seconds (UCUM unit "s").
    /// Callers must record <c>sw.Elapsed.TotalSeconds</c>, not <c>sw.ElapsedMilliseconds</c>.
    /// The instrument name does not encode the unit; use the declared unit metadata instead.
    /// </remarks>
    public static readonly Histogram<double> ProcessingDuration = Shared.ProcessingDuration;

    /// <summary>
    /// Histogram for the time from an outbox entry being written to its message being handed
    /// to the transport. Measured on the publish that succeeded, so retries show up as latency.
    /// </summary>
    public static readonly Histogram<double> OutboxDeliveryLatency = Shared.OutboxDeliveryLatency;

    /// <summary>
    /// Histogram for how old an event is, measured from its <c>CreatedAt</c>, when a processor
    /// starts handling it. Unlike lag, which counts events, this is the delay a reader of the
    /// projection actually experiences.
    /// </summary>
    public static readonly Histogram<double> EventAge = Shared.EventAge;

    #endregion

    #region Activity Names

    /// <summary>
    /// Activity name for append operations.
    /// </summary>
    public const string AppendActivityName = "append";

    /// <summary>
    /// Activity name for consume operations.
    /// </summary>
    public const string ConsumeActivityName = "process";

    #endregion
}
