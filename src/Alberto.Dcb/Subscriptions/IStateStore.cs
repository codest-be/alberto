using System.Data;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Transaction-aware state storage for projections.
/// Supports both inline projections (with transaction) and async projections (without).
/// </summary>
/// <typeparam name="TState">The type of state being stored.</typeparam>
public interface IStateStore<TState>
{
    /// <summary>
    /// Loads multiple states by their document IDs.
    /// </summary>
    /// <param name="documentIds">The document IDs to load.</param>
    /// <param name="transaction">Optional transaction for inline projections.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of document ID to state for found documents.</returns>
    Task<Dictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        IDbTransaction? transaction = null,
        CancellationToken ct = default);

    /// <summary>
    /// Applies upserts and deletes to the state store.
    /// </summary>
    /// <param name="upserts">States to insert or update, keyed by document ID.</param>
    /// <param name="deletes">Document IDs to delete.</param>
    /// <param name="transaction">Optional transaction for inline projections.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        IDbTransaction? transaction = null,
        CancellationToken ct = default);
}
