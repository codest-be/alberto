namespace Alberto.EventStore.Subscriptions.Batching;

/// <summary>
/// Configuration options for projection batching behavior.
/// Controls when accumulated projection updates are flushed to the repository.
/// </summary>
public class ProjectionBatchingOptions
{
    /// <summary>
    /// Maximum number of events to accumulate before flushing (for async mode).
    /// Default: 100 events
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum time to wait before flushing accumulated events (for async mode).
    /// Default: 100 milliseconds
    /// </summary>
    public TimeSpan MaxBatchTime { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Save strategy for this projection.
    /// Default: Auto (sync=immediate, async=batched, polling=per-cycle)
    /// </summary>
    public ProjectionSaveStrategy SaveStrategy { get; set; } = ProjectionSaveStrategy.Auto;
}

/// <summary>
/// Determines when projection updates are saved to the repository.
/// </summary>
public enum ProjectionSaveStrategy
{
    /// <summary>
    /// Auto-detect based on subscription mode:
    /// - Sync: Immediate save after each event (strong consistency)
    /// - Async: Batched using MaxBatchSize and MaxBatchTime thresholds
    /// - Polling: Batched per poll cycle (all fetched events)
    /// </summary>
    Auto,

    /// <summary>
    /// Always save immediately after each event (like sync mode).
    /// Use for projections requiring strong consistency.
    /// </summary>
    Immediate,

    /// <summary>
    /// Always batch using MaxBatchSize and MaxBatchTime thresholds (like async mode).
    /// Use for projections where eventual consistency is acceptable.
    /// </summary>
    Batched
}