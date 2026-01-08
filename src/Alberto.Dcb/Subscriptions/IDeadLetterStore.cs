namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Storage for events that failed processing.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>
    /// Stores a failed event in dead letter storage.
    /// </summary>
    Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Gets dead letter entries for a processor.
    /// </summary>
    /// <param name="processorId">The processor identifier.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the count of dead letter entries for a processor.
    /// </summary>
    Task<int> CountAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Removes a dead letter entry (e.g., after successful replay).
    /// </summary>
    Task RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Removes all dead letter entries for a processor.
    /// </summary>
    Task ClearAsync(string processorId, CancellationToken ct = default);
}
