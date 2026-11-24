namespace Alberto.Projections;

/// <summary>
/// Loads a batch's documents, folds the batch onto them and applies the outcome in one unit of
/// work, so a store that would otherwise read the documents again to write them (EF re-queries
/// tracked entities) reads them once. Internal and optional: a store that does not implement it
/// gets <see cref="IStateStore{TState}.LoadManyAsync"/> then
/// <see cref="IStateStore{TState}.ApplyChangesAsync"/>, which behaves the same.
/// </summary>
internal interface IStateStoreBatchFold<TState>
{
    /// <summary>
    /// Loads <paramref name="documentIds"/>, hands them to <paramref name="fold"/> and applies the
    /// upserts and deletes it returns with the same guarantees as
    /// <see cref="IStateStore{TState}.ApplyChangesAsync"/>. Returns whether anything was applied.
    /// </summary>
    /// <remarks>
    /// <paramref name="fold"/> runs exactly once; a retried write reuses its outcome rather than
    /// folding again, as a caller retrying <see cref="IStateStore{TState}.ApplyChangesAsync"/> would.
    /// </remarks>
    Task<bool> LoadFoldApplyAsync(
        IReadOnlyList<string> documentIds,
        Func<IReadOnlyDictionary<string, TState>, (IReadOnlyDictionary<string, TState> Upserts, IReadOnlyCollection<string> Deletes)> fold,
        CancellationToken cancellationToken);
}
