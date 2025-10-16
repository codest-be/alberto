using System.Diagnostics.Metrics;

namespace Alberto.EventStore.Telemetry;

/// <summary>
/// Provides OpenTelemetry metrics for the Alberto event store
/// </summary>
internal static class AlbertoMeter
{
    /// <summary>
    /// Gets the name of the meter for this library.
    /// </summary>
    public static string Name => "Alberto";

    private static string Version { get; } =
        typeof(AlbertoActivitySource).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Gets the meter for this library.
    /// </summary>
    public static Meter Source { get; } = new(Name, Version);

    // Event Store Operations
    public static Counter<long> EventsAppended { get; } = Source.CreateCounter<long>(
        "alberto.events.appended",
        unit: "{event}",
        description: "Total number of events appended to the event store");

    public static Histogram<double> AppendDuration { get; } = Source.CreateHistogram<double>(
        "alberto.events.append.duration",
        unit: "ms",
        description: "Duration of event append operations");

    public static Counter<long> EventsQueried { get; } = Source.CreateCounter<long>(
        "alberto.events.queried",
        unit: "{event}",
        description: "Total number of events queried from the event store");

    public static Histogram<double> QueryDuration { get; } = Source.CreateHistogram<double>(
        "alberto.events.query.duration",
        unit: "ms",
        description: "Duration of event query operations");

    // Background Processing
    public static Counter<long> EventsProcessed { get; } = Source.CreateCounter<long>(
        "alberto.subscription.events.processed",
        unit: "{event}",
        description: "Total number of events processed by subscriptions");

    public static Histogram<double> ProcessingDuration { get; } = Source.CreateHistogram<double>(
        "alberto.subscription.processing.duration",
        unit: "ms",
        description: "Duration of event processing in subscriptions");

    public static Histogram<double> ProcessingLatency { get; } = Source.CreateHistogram<double>(
        "alberto.subscription.latency",
        unit: "ms",
        description: "Time between event creation and processing completion");

    // Observable gauges will be created by OpenTelemetryMetricsRecorder with callbacks

    public static Histogram<int> PollingBatchSize { get; } = Source.CreateHistogram<int>(
        "alberto.polling.batch_size",
        unit: "{event}",
        description: "Number of events retrieved per polling batch");

    // Retries & Poison Pills
    public static Counter<long> Retries { get; } = Source.CreateCounter<long>(
        "alberto.subscription.retries",
        unit: "{attempt}",
        description: "Total number of retry attempts for failed event processing");

    public static Counter<long> PoisonPillsCreated { get; } = Source.CreateCounter<long>(
        "alberto.subscription.poison_pills.created",
        unit: "{poisonpill}",
        description: "Total number of poison pills created");

    // Channel Operations
    public static Counter<long> ChannelEventsPublished { get; } = Source.CreateCounter<long>(
        "alberto.channel.events.published",
        unit: "{event}",
        description: "Total number of events published to channels");

    public static Counter<long> ChannelWriteBlocked { get; } = Source.CreateCounter<long>(
        "alberto.channel.write.blocked",
        unit: "{operation}",
        description: "Number of times channel write was blocked requiring async write");
}