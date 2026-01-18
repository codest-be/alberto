namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Store for managing rebuild version metadata.
/// Tracks active and rebuilding versions for zero-downtime projection rebuilds.
/// </summary>
public interface IRebuildMetadataStore
{
    /// <summary>
    /// Gets the active version for a projection type.
    /// Returns 1 if no metadata exists (default version).
    /// </summary>
    /// <param name="projectionType">The projection type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The active version number (default 1).</returns>
    Task<int> GetActiveVersionAsync(string projectionType, CancellationToken ct = default);

    /// <summary>
    /// Gets the full rebuild metadata for a projection type.
    /// </summary>
    /// <param name="projectionType">The projection type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The metadata, or null if no rebuild has been tracked.</returns>
    Task<RebuildMetadata?> GetAsync(string projectionType, CancellationToken ct = default);

    /// <summary>
    /// Saves rebuild metadata for a projection type.
    /// </summary>
    /// <param name="metadata">The metadata to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(RebuildMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Deletes rebuild metadata for a projection type.
    /// </summary>
    /// <param name="projectionType">The projection type identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string projectionType, CancellationToken ct = default);
}

/// <summary>
/// Metadata for tracking projection rebuild state.
/// </summary>
public sealed record RebuildMetadata
{
    /// <summary>
    /// The projection type this metadata belongs to.
    /// </summary>
    public required string ProjectionType { get; init; }

    /// <summary>
    /// The currently active version that queries read from.
    /// </summary>
    public int ActiveVersion { get; init; } = 1;

    /// <summary>
    /// The version currently being rebuilt (null if not rebuilding).
    /// </summary>
    public int? RebuildingVersion { get; init; }

    /// <summary>
    /// The current status of the rebuild operation.
    /// </summary>
    public RebuildOperationStatus? RebuildStatus { get; init; }

    /// <summary>
    /// When the rebuild was started.
    /// </summary>
    public DateTimeOffset? RebuildStartedAt { get; init; }

    /// <summary>
    /// The target global position for the rebuild (captured at start).
    /// </summary>
    public long? RebuildTargetPosition { get; init; }

    /// <summary>
    /// When the rebuild completed (success or failure).
    /// </summary>
    public DateTimeOffset? RebuildCompletedAt { get; init; }
}

/// <summary>
/// Status of a versioned rebuild operation.
/// </summary>
public enum RebuildOperationStatus
{
    /// <summary>
    /// Rebuild is in progress, processor is catching up.
    /// </summary>
    Rebuilding,

    /// <summary>
    /// Rebuild has caught up and is ready to be swapped.
    /// </summary>
    ReadyToSwap,

    /// <summary>
    /// Rebuild completed and was swapped to active.
    /// </summary>
    Swapped,

    /// <summary>
    /// Rebuild was rolled back (cancelled or failed).
    /// </summary>
    RolledBack
}
