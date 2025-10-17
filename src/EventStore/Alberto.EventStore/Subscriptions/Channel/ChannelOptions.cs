namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Configuration options for channel-based subscriptions
/// </summary>
public sealed class ChannelOptions
{
    /// <summary>
    /// Maximum capacity for bounded channels. Default: 10,000 events.
    /// Channels use BoundedChannelFullMode.Wait to provide backpressure when full.
    /// This prevents unbounded memory growth under sustained load with slow handlers.
    /// Increase this value if you have high-throughput scenarios with many concurrent subscriptions.
    /// </summary>
    public int BoundedCapacity { get; set; } = 10_000;

    /// <summary>
    /// Maximum number of retries for failed event handling. Default: 3
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Delay in milliseconds between retries. Default: 500ms
    /// </summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>
    /// Whether to process events in parallel across handlers. Default: false
    /// </summary>
    public bool AllowParallelExecution { get; set; }
}