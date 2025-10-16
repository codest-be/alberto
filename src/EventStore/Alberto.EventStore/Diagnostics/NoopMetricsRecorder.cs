namespace Alberto.EventStore.Diagnostics;

/// <summary>
/// No-op implementation of metrics recorder when telemetry is not enabled
/// </summary>
public sealed class NoopMetricsRecorder : IMetricsRecorder
{
    public IDisposable RecordAppend(string tenantId, string schema, int eventCount) => NoopDisposable.Instance;

    public void RecordEventAppended(string tenantId, string eventType, string schema) { }

    public IDisposable RecordQuery(string schema, bool hasFilters) => NoopDisposable.Instance;

    public void RecordEventsQueried(int count, string schema, bool hasFilters) { }

    public IDisposable RecordEventProcessing(string subscriptionId, string eventType) => NoopDisposable.Instance;

    public void RecordEventProcessed(string subscriptionId, string eventType, DateTimeOffset eventCreated) { }

    public void RecordEventProcessingFailed(string subscriptionId, string eventType) { }

    public void RecordRetry(string subscriptionId, string eventType, int attempt) { }

    public void RecordPoisonPill(string subscriptionId, string eventType) { }

    public void RecordPollingBatch(string moduleKey, int eventCount) { }

    public void UpdatePollingInterval(string moduleKey, int intervalMs) { }

    public void UpdateSubscriptionPosition(string subscriptionId, long position, long maxGlobalPosition) { }

    public void RecordChannelPublish(string moduleKey, int eventCount) { }

    public void RecordChannelWriteBlocked(string moduleKey) { }

    public void UpdateChannelDepth(string moduleKey, int depth) { }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}