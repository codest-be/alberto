using System.Diagnostics;
using System.Diagnostics.Metrics;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.PoisonPills;

namespace Alberto.EventStore.Telemetry;

/// <summary>
/// OpenTelemetry implementation of metrics recorder
/// </summary>
public sealed class OpenTelemetryMetricsRecorder : IMetricsRecorder
{
    private readonly Dictionary<string, int> _channelDepths = new();
    private readonly IPoisonPillStore? _poisonPillStore;
    private readonly Dictionary<string, int> _pollingIntervals = new();
    private readonly Dictionary<string, SubscriptionMetrics> _subscriptionMetrics = new();

    public OpenTelemetryMetricsRecorder(IPoisonPillStore? poisonPillStore = null)
    {
        _poisonPillStore = poisonPillStore;

        // Create observable gauges with callbacks
        AlbertoMeter.Source.CreateObservableGauge(
            "alberto.subscription.lag",
            () => GetSubscriptionLagMeasurements(),
            unit: "{event}",
            description: "Number of events between subscription checkpoint and latest global position");

        AlbertoMeter.Source.CreateObservableGauge(
            "alberto.polling.interval",
            () => GetPollingIntervalMeasurements(),
            unit: "ms",
            description: "Current polling interval for subscription polling service");

        AlbertoMeter.Source.CreateObservableGauge(
            "alberto.channel.depth",
            () => GetChannelDepthMeasurements(),
            unit: "{event}",
            description: "Number of events waiting in channel");
    }

    public IDisposable RecordAppend(string tenantId, string schema, int eventCount)
    {
        var stopwatch = Stopwatch.StartNew();
        var batchSizeBucket = eventCount switch
        {
            1 => "1",
            <= 5 => "5",
            <= 10 => "10",
            <= 100 => "100",
            _ => "1000+"
        };

        return new AppendScope(stopwatch, tenantId, schema, batchSizeBucket);
    }

    public void RecordEventAppended(string tenantId, string eventType, string schema)
    {
        AlbertoMeter.EventsAppended.Add(
            1,
            new KeyValuePair<string, object?>("tenant_id", tenantId),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("schema", schema));
    }

    public IDisposable RecordQuery(string schema, bool hasFilters)
    {
        var stopwatch = Stopwatch.StartNew();
        return new QueryScope(stopwatch, schema, hasFilters);
    }

    public void RecordEventsQueried(int count, string schema, bool hasFilters)
    {
        AlbertoMeter.EventsQueried.Add(
            count,
            new KeyValuePair<string, object?>("schema", schema),
            new KeyValuePair<string, object?>("has_filters", hasFilters));
    }

    public IDisposable RecordEventProcessing(string subscriptionId, string eventType)
    {
        var stopwatch = Stopwatch.StartNew();
        return new ProcessingScope(stopwatch, subscriptionId, eventType);
    }

    public void RecordEventProcessed(string subscriptionId, string eventType, DateTimeOffset eventCreated)
    {
        AlbertoMeter.EventsProcessed.Add(
            1,
            new KeyValuePair<string, object?>("subscription_id", subscriptionId),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("status", "success"));

        // Record latency (time from event creation to processing completion)
        var latency = DateTimeOffset.UtcNow - eventCreated;
        AlbertoMeter.ProcessingLatency.Record(
            latency.TotalMilliseconds,
            new KeyValuePair<string, object?>("subscription_id", subscriptionId));
    }

    public void RecordEventProcessingFailed(string subscriptionId, string eventType)
    {
        AlbertoMeter.EventsProcessed.Add(
            1,
            new KeyValuePair<string, object?>("subscription_id", subscriptionId),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("status", "failure"));
    }

    public void RecordRetry(string subscriptionId, string eventType, int attempt)
    {
        AlbertoMeter.Retries.Add(
            1,
            new KeyValuePair<string, object?>("subscription_id", subscriptionId),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("attempt", attempt));
    }

    public void RecordPoisonPill(string subscriptionId, string eventType)
    {
        AlbertoMeter.PoisonPillsCreated.Add(
            1,
            new KeyValuePair<string, object?>("subscription_id", subscriptionId),
            new KeyValuePair<string, object?>("event_type", eventType));
    }

    public void RecordPollingBatch(string moduleKey, int eventCount)
    {
        AlbertoMeter.PollingBatchSize.Record(
            eventCount,
            new KeyValuePair<string, object?>("module_key", moduleKey));
    }

    public void UpdatePollingInterval(string moduleKey, int intervalMs)
    {
        lock (_pollingIntervals)
        {
            _pollingIntervals[moduleKey] = intervalMs;
        }
    }

    public void UpdateSubscriptionPosition(string subscriptionId, long position, long maxGlobalPosition)
    {
        lock (_subscriptionMetrics)
        {
            _subscriptionMetrics[subscriptionId] = new SubscriptionMetrics(position, maxGlobalPosition);
        }
    }

    public void RecordChannelPublish(string moduleKey, int eventCount)
    {
        AlbertoMeter.ChannelEventsPublished.Add(
            eventCount,
            new KeyValuePair<string, object?>("module_key", moduleKey));
    }

    public void RecordChannelWriteBlocked(string moduleKey)
    {
        AlbertoMeter.ChannelWriteBlocked.Add(
            1,
            new KeyValuePair<string, object?>("module_key", moduleKey));
    }

    public void UpdateChannelDepth(string moduleKey, int depth)
    {
        lock (_channelDepths)
        {
            _channelDepths[moduleKey] = depth;
        }
    }

    private IEnumerable<Measurement<long>> GetSubscriptionLagMeasurements()
    {
        lock (_subscriptionMetrics)
        {
            foreach (var (subscriptionId, metrics) in _subscriptionMetrics)
            {
                var lag = metrics.MaxGlobalPosition - metrics.Position;
                yield return new Measurement<long>(
                    lag,
                    new KeyValuePair<string, object?>("subscription_id", subscriptionId));
            }
        }
    }

    private IEnumerable<Measurement<int>> GetPollingIntervalMeasurements()
    {
        lock (_pollingIntervals)
        {
            foreach (var (moduleKey, interval) in _pollingIntervals)
            {
                yield return new Measurement<int>(
                    interval,
                    new KeyValuePair<string, object?>("module_key", moduleKey));
            }
        }
    }

    private IEnumerable<Measurement<int>> GetChannelDepthMeasurements()
    {
        lock (_channelDepths)
        {
            foreach (var (moduleKey, depth) in _channelDepths)
            {
                yield return new Measurement<int>(
                    depth,
                    new KeyValuePair<string, object?>("module_key", moduleKey));
            }
        }
    }

    private sealed record SubscriptionMetrics(long Position, long MaxGlobalPosition);

    private sealed class AppendScope : IDisposable
    {
        private readonly string _batchSizeBucket;
        private readonly string _schema;
        private readonly Stopwatch _stopwatch;
        private readonly string _tenantId;

        public AppendScope(Stopwatch stopwatch, string tenantId, string schema, string batchSizeBucket)
        {
            _stopwatch = stopwatch;
            _tenantId = tenantId;
            _schema = schema;
            _batchSizeBucket = batchSizeBucket;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            AlbertoMeter.AppendDuration.Record(
                _stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tenant_id", _tenantId),
                new KeyValuePair<string, object?>("schema", _schema),
                new KeyValuePair<string, object?>("batch_size", _batchSizeBucket));
        }
    }

    private sealed class QueryScope : IDisposable
    {
        private readonly bool _hasFilters;
        private readonly string _schema;
        private readonly Stopwatch _stopwatch;

        public QueryScope(Stopwatch stopwatch, string schema, bool hasFilters)
        {
            _stopwatch = stopwatch;
            _schema = schema;
            _hasFilters = hasFilters;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            AlbertoMeter.QueryDuration.Record(
                _stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("schema", _schema),
                new KeyValuePair<string, object?>("has_filters", _hasFilters));
        }
    }

    private sealed class ProcessingScope : IDisposable
    {
        private readonly string _eventType;
        private readonly Stopwatch _stopwatch;
        private readonly string _subscriptionId;

        public ProcessingScope(Stopwatch stopwatch, string subscriptionId, string eventType)
        {
            _stopwatch = stopwatch;
            _subscriptionId = subscriptionId;
            _eventType = eventType;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            AlbertoMeter.ProcessingDuration.Record(
                _stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("subscription_id", _subscriptionId),
                new KeyValuePair<string, object?>("event_type", _eventType));
        }
    }
}