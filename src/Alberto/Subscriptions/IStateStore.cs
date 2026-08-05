namespace Alberto.Subscriptions;

/// <summary>
/// State storage used by projection processors.
/// Each adapter owns the transaction required to apply a set of changes atomically;
/// see <see cref="ApplyChangesAsync"/> for the exact atomicity guarantee every adapter
/// must honour.
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
    /// <exception cref="ArgumentNullException"><paramref name="documentIds"/> is null.</exception>
    Task<IReadOnlyDictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default);

    /// <summary>
    /// Applies upserts and deletes to the state store in one atomic batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Atomicity.</strong> No concurrent read through this store observes the batch
    /// partially applied. A reader sees either every upsert and delete in the batch or none —
    /// never a subset. The boundary that enforces this is the adapter's own transaction or lock
    /// region, not the caller's, which is why there is no <c>ITransaction</c> argument: the
    /// guarantee cannot be delegated outward without losing it.
    /// </para>
    /// <para>
    /// <strong>Same document ID in both collections.</strong> Delete wins. When a document ID
    /// appears in both <paramref name="upserts"/> and <paramref name="deletes"/>, the document
    /// is absent after the batch completes. The precise mechanism varies by adapter — Postgres
    /// and InMemory execute the upsert first and the delete last within the same atomic boundary;
    /// EF suppresses the upsert so the delete runs uncontested — but the observable outcome is
    /// identical: a batch that intends to drop a document cannot accidentally resurrect it.
    /// </para>
    /// <para>
    /// <strong>Empty is not null.</strong> Either collection may be empty — an upsert-only or
    /// delete-only batch is ordinary — but neither may be null. A null collection is a caller
    /// bug rather than an empty batch, so every adapter rejects it up front with
    /// <see cref="ArgumentNullException"/> instead of failing later, or differently, once the
    /// batch is already under way.
    /// </para>
    /// </remarks>
    /// <param name="upserts">States to insert or update, keyed by document ID.</param>
    /// <param name="deletes">Document IDs to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="upserts"/> or <paramref name="deletes"/> is null.
    /// </exception>
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
