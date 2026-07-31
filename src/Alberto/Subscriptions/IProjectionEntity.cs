namespace Alberto.Subscriptions;

/// <summary>
/// Base interface for projection entities.
/// Provides the required columns for document identification,
/// idempotency tracking, and optimistic concurrency.
/// </summary>
public interface IProjectionEntity
{
    /// <summary>
    /// The document ID that uniquely identifies this entity.
    /// Part of the composite primary key.
    /// </summary>
    string DocumentId { get; set; }

    /// <summary>
    /// Timestamp of the last update to this entity.
    /// Used for ordering and tracking changes.
    /// </summary>
    DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The global position of the last event processed for this entity.
    /// Used for idempotency - events with positions less than or equal to this are skipped.
    /// </summary>
    long LastProcessedPosition { get; set; }

    /// <summary>
    /// Row version for optimistic concurrency.
    /// For PostgreSQL, map this to the xmin system column.
    /// </summary>
    uint Version { get; set; }

    /// <summary>
    /// The rebuild version for zero-downtime rebuilds.
    /// Allows multiple versions of projection data to coexist during rebuilds.
    /// Default is 1. During rebuild, new data is written to version N+1.
    /// </summary>
    int RebuildVersion { get; set; }
}
