namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Interface for clearing projection state during rebuilds.
/// Implementations are registered automatically when using AddEfProjection
/// and are called by the admin service when starting a rebuild with clearState=true.
/// </summary>
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
