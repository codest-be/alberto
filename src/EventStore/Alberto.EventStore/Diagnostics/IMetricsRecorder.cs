namespace Alberto.EventStore.Diagnostics;

/// <summary>
/// Interface for recording event store metrics
/// </summary>
public interface IMetricsRecorder
{
    /// <summary>
    /// Records event append operation metrics
    /// </summary>
    IDisposable RecordAppend(string tenantId, string schema, int eventCount);

    /// <summary>
    /// Records individual event appended
    /// </summary>
    void RecordEventAppended(string tenantId, string eventType, string schema);

    /// <summary>
    /// Records event query operation metrics
    /// </summary>
    IDisposable RecordQuery(string schema, bool hasFilters);

    /// <summary>
    /// Records events queried count
    /// </summary>
    void RecordEventsQueried(int count, string schema, bool hasFilters);

    /// <summary>
    /// Records subscription event processing
    /// </summary>
    IDisposable RecordEventProcessing(string subscriptionId, string eventType);

    /// <summary>
    /// Records successful event processing with latency
    /// </summary>
    void RecordEventProcessed(string subscriptionId, string eventType, DateTimeOffset eventCreated);

    /// <summary>
    /// Records failed event processing
    /// </summary>
    void RecordEventProcessingFailed(string subscriptionId, string eventType);

    /// <summary>
    /// Records a retry attempt
    /// </summary>
    void RecordRetry(string subscriptionId, string eventType, int attempt);

    /// <summary>
    /// Records poison pill creation
    /// </summary>
    void RecordPoisonPill(string subscriptionId, string eventType);

    /// <summary>
    /// Records polling batch size
    /// </summary>
    void RecordPollingBatch(string moduleKey, int eventCount);

    /// <summary>
    /// Updates current polling interval
    /// </summary>
    void UpdatePollingInterval(string moduleKey, int intervalMs);

    /// <summary>
    /// Updates subscription position for lag calculation
    /// </summary>
    void UpdateSubscriptionPosition(string subscriptionId, long position, long maxGlobalPosition);

    /// <summary>
    /// Records channel event publication
    /// </summary>
    void RecordChannelPublish(string moduleKey, int eventCount);

    /// <summary>
    /// Records channel write blocked (had to use async write)
    /// </summary>
    void RecordChannelWriteBlocked(string moduleKey);

    /// <summary>
    /// Updates channel depth (for bounded channels)
    /// </summary>
    void UpdateChannelDepth(string moduleKey, int depth);
}