namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Interface for clearing projection state during rebuilds.
/// Implementations are registered automatically when using AddEfProjection.
/// </summary>
/// <remarks>
/// Nothing in this repository resolves this service yet. The rebuild pipeline it belongs to
/// is scaffolding: <c>alberto_projection_rebuild_meta</c> is never read or written, and
/// <c>PostgresStateStore</c>'s rebuild version is always its default of 1. <c>alberto ops rebuild</c>
/// resets the checkpoint only. Wire a caller before relying on state being cleared on rebuild.
/// </remarks>
public interface IProjectionStateClearer
{
    /// <summary>
    /// The processor ID this clearer is associated with.
    /// </summary>
    string ProcessorId { get; }

    /// <summary>
    /// Clears all projection state for this processor.
    /// For EF projections, this deletes all entities from the backing table.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}
