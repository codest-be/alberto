namespace Alberto.EventSourcing.Projections;

/// <summary>
/// Marker interface for projection entities that track the global event position for idempotency.
/// When a projection implements this interface, the repository can ensure that events are not processed twice.
/// </summary>
public interface IVersionedProjection
{
    /// <summary>
    /// The global position of the last event that updated this projection.
    /// Used to ensure idempotency - events with a position less than or equal to this value are skipped.
    /// </summary>
    long GlobalVersion { get; set; }
}