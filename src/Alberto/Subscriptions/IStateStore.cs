namespace Alberto.Subscriptions;

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
    Task<IReadOnlyDictionary<string, TState>> LoadManyAsync(
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

    /// <summary>
    /// Lists the most recently updated states this store can see, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped exactly as <see cref="LoadManyAsync"/> is: same tenant, same rebuild version. A
    /// reader that wants one tenant's documents obtains a store for that tenant; there is no
    /// per-call tenant argument, because a store whose tenancy could change per call could not
    /// decide which SQL to emit.
    /// </para>
    /// <para>
    /// "Recently updated" means by the store's own write timestamp, not by any field of
    /// <typeparamref name="TState"/>. Documents written in the same batch have no guaranteed
    /// order relative to each other.
    /// </para>
    /// <para>
    /// This is the read-side counterpart to <see cref="LoadManyAsync"/>, which needs the
    /// document ids up front. It lives on the interface rather than on one adapter so that a
    /// resolver listing a projection does not have to name a concrete store type — naming one
    /// is what let readers and writers disagree about tenancy in the first place.
    /// </para>
    /// </remarks>
    /// <param name="limit">Maximum number of states to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TState>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default);
}
