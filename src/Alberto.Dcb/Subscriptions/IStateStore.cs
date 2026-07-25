namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// State storage used by projection processors.
/// Each adapter owns the transaction required to apply a set of changes atomically.
/// </summary>
/// <typeparam name="TState">The type of state being stored.</typeparam>
public interface IStateStore<TState>
{
    /// <summary>
    /// Loads multiple states by their document IDs.
    /// </summary>
    /// <param name="documentIds">The document IDs to load.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of document ID to state for found documents.</returns>
    Task<Dictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default);

    /// <summary>
    /// Applies upserts and deletes to the state store.
    /// </summary>
    /// <param name="upserts">States to insert or update, keyed by document ID.</param>
    /// <param name="deletes">Document IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default);
}
