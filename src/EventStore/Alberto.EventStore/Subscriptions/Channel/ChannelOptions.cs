namespace Alberto.EventStore.Subscriptions.Channel;

/// <summary>
/// Configuration options for channel-based subscriptions
/// </summary>
public sealed class ChannelOptions
{
    /// <summary>
    /// Maximum capacity for bounded channels. If null, unbounded channels are used. Default: null (unbounded)
    /// </summary>
    public int? BoundedCapacity { get; set; }

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