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

    #endregion

    #region Gauges

    /// <summary>
    /// Observable gauge for processor lag (distance from global position).
    /// </summary>
    public static readonly ObservableGauge<long> ProcessorLag =
        Meter.CreateObservableGauge("alberto.processor.lag", GetProcessorLagMeasurements, "events", "Number of events a processor is behind the global position");

    private static readonly List<Measurement<long>> _processorLagMeasurements = [];
    private static readonly object _measurementsLock = new();

    private static IEnumerable<Measurement<long>> GetProcessorLagMeasurements()
    {
        lock (_measurementsLock) { return _processorLagMeasurements.ToArray(); }
    }

    /// <summary>
    /// Updates processor lag measurements for observable gauge.
    /// </summary>
    public static void RecordProcessorLag(string processorId, string module, long lag)
    {
        lock (_measurementsLock)
        {
            _processorLagMeasurements.RemoveAll(m =>
                m.Tags.ToArray().Any(t => t.Key == "processor" && t.Value?.ToString() == processorId));
            _processorLagMeasurements.Add(new Measurement<long>(lag,
                new KeyValuePair<string, object?>("processor", processorId),
                new KeyValuePair<string, object?>("module", module)));
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
