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
    /// Maximum number of poison pill check results to cache in-memory.
    /// Caching reduces database queries since most checks return null (no poison pill).
    /// </summary>
    public int PoisonPillCacheSize { get; set; } = 10000;
}