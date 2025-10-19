namespace Alberto.EventStore.Subscriptions.Polling;

/// <summary>
/// Configuration options for subscription polling
/// </summary>
public sealed class PollingOptions
{
    /// <summary>
    /// Minimum polling interval in milliseconds when events are not found
    /// </summary>
    public int MinPollingIntervalMs { get; set; } = 25;

    /// <summary>
    /// Maximum polling interval in milliseconds
    /// </summary>
    public int MaxPollingIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Maximum number of events to retrieve in a single page
    /// </summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// Factor by which polling interval grows when no events are found
    /// </summary>
    public double PollingGrowFactor { get; set; } = 1.5;

    /// <summary>
    /// Maximum number of retries for failed events before creating poison pill
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds for retries (multiplied by attempt number)
    /// </summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Interval in seconds for flushing checkpoint updates to database.
    /// Checkpoints are cached in-memory and periodically flushed to reduce database load.
    /// </summary>
    public int CheckpointFlushIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// [OBSOLETE] This property is no longer used. The poison pill cache now uses automatic position-based eviction
    /// and automatically manages its size based on subscription progress.
    /// </summary>
    [Obsolete("The poison pill cache now uses automatic position-based eviction. This property is no longer used and will be removed in a future version.")]
    public int PoisonPillCacheSize { get; set; } = 10000;

    /// <summary>
    /// Maximum number of events to process in a single batch for projection updates.
    /// Higher values improve throughput by reducing database roundtrips, but increase memory usage.
    /// Set to 1 to disable batching (process events one at a time).
    /// </summary>
    public int ProjectionBatchSize { get; set; } = 50;

    /// <summary>
    /// Initial retry interval in milliseconds when failing to acquire the distributed lock.
    /// When multiple instances compete for the lock, failed attempts will retry with exponential backoff.
    /// </summary>
    public int LockAcquisitionRetryIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Maximum retry interval in milliseconds when failing to acquire the distributed lock.
    /// Prevents unbounded growth of the backoff interval.
    /// </summary>
    public int LockAcquisitionMaxRetryIntervalMs { get; set; } = 60000;

    /// <summary>
    /// Factor by which the lock acquisition retry interval grows on each failed attempt.
    /// </summary>
    public double LockRetryBackoffFactor { get; set; } = 1.5;
}