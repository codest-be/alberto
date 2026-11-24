using Alberto.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Alberto.EntityFramework;

/// <summary>
/// The one recipe for writing a batch of projection documents through EF: load the batch's rows
/// of one rebuild version tracked, fold, stamp, stage onto the tracked rows and save. The async
/// <see cref="EfStateStore{TEntity, TDbContext}"/> and the inline EF projection differ only in
/// whose context this runs on and what happens when a save conflicts.
/// </summary>
/// <remarks>
/// Staging copies the new values onto the tracked rows rather than updating a detached instance,
/// so EF diffs the owned types and owned collections against what it loaded instead of needing
/// the shadow keys of owned rows it never saw.
/// </remarks>
internal static class EfProjectionWriter
{
    /// <summary>
    /// Loads <paramref name="documentIds"/> tracked, folds, and saves the outcome through the same
    /// context. Not saved, and left for the caller to write on a fresh read, when the fold changed
    /// a loaded document in place and then did not write it: change tracking would persist that,
    /// where a read-then-write never did.
    /// </summary>
    internal static async Task<EfFoldOutcome<TEntity>> LoadFoldSaveAsync<TEntity>(
        DbContext context,
        int version,
        IReadOnlyList<string> documentIds,
        Func<IReadOnlyDictionary<string, TEntity>, (IReadOnlyDictionary<string, TEntity> Upserts, IReadOnlyCollection<string> Deletes)> fold,
        CancellationToken cancellationToken)
        where TEntity : class, IProjectionEntity, new()
    {
        var existing = await LoadAsync<TEntity>(context, documentIds, version, cancellationToken);

        var (upserts, deletes) = fold(existing);
        if (upserts.Count == 0 && deletes.Count == 0)
            return new EfFoldOutcome<TEntity>(upserts, deletes, Saved: false);

        var untouched = existing.Keys.Any(id => !upserts.ContainsKey(id) && !deletes.Contains(id));
        if (untouched && context.ChangeTracker.HasChanges())
            return new EfFoldOutcome<TEntity>(upserts, deletes, Saved: false);

        Stage(context, existing, upserts, deletes, version);
        await context.SaveChangesAsync(cancellationToken);
        return new EfFoldOutcome<TEntity>(upserts, deletes, Saved: true);
    }

    /// <summary>
    /// Writes an already folded change set: loads the rows it touches tracked, stages and saves.
    /// </summary>
    internal static async Task WriteAsync<TEntity>(
        DbContext context,
        int version,
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken cancellationToken)
        where TEntity : class, IProjectionEntity, new()
    {
        var allDocIds = upserts.Keys.Concat(deletes).Distinct().ToList();
        var existing = await LoadAsync<TEntity>(context, allDocIds, version, cancellationToken);

        Stage(context, existing, upserts, deletes, version);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static Task<Dictionary<string, TEntity>> LoadAsync<TEntity>(
        DbContext context,
        IReadOnlyList<string> ids,
        int version,
        CancellationToken cancellationToken)
        where TEntity : class, IProjectionEntity, new()
    {
        var idList = ids as List<string> ?? ids.ToList();
        return context.Set<TEntity>()
            .IgnoreQueryFilters([ProjectionQueryFilters.ActiveVersion])
            .Where(e => idList.Contains(e.DocumentId) && e.RebuildVersion == version)
            .ToDictionaryAsync(e => e.DocumentId, cancellationToken);
    }

    /// <summary>
    /// Stages a change set on a context that tracks the batch's existing rows.
    /// </summary>
    private static void Stage<TEntity>(
        DbContext context,
        IReadOnlyDictionary<string, TEntity> existingEntities,
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        int version)
        where TEntity : class, IProjectionEntity, new()
    {
        // Delete-wins: a document ID present in both upserts and deletes must be absent
        // after the batch. EF cannot express "insert then delete in one statement" the way
        // the Postgres adapter does (upsert SQL followed by delete SQL in one transaction),
        // so the same result is achieved by suppressing any upsert whose ID is also being
        // deleted — the delete then runs uncontested, whether the row exists or not.
        var deleteSet = deletes.Count > 0 ? new HashSet<string>(deletes) : null;

        foreach (var docId in deletes)
        {
            if (existingEntities.TryGetValue(docId, out var existing))
                context.Set<TEntity>().Remove(existing);
        }

        foreach (var (docId, newEntity) in upserts)
        {
            if (deleteSet?.Contains(docId) == true)
                continue; // delete wins; skip the upsert for this document

            ProjectionEntityStamp.DocumentId(newEntity, docId);
            // Stamped rather than left to the column default. A loaded row already carries the
            // right version, but a declaration that returns a fresh instance leaves it at the
            // CLR default, and relying on the store default to rewrite that only works while
            // the live version happens to be 1.
            ProjectionEntityStamp.RebuildVersion(newEntity, version);
            ProjectionEntityStamp.UpdatedAt(newEntity, DateTimeOffset.UtcNow);

            if (existingEntities.TryGetValue(docId, out var existing))
            {
                context.Entry(existing).CurrentValues.SetValues(newEntity);
                CopyOwnedNavigations(context.Entry(existing), newEntity);
            }
            else
            {
                context.Set<TEntity>().Add(newEntity);
            }
        }
    }

    /// <summary>
    /// Copies owned-navigation values from <paramref name="source"/> onto a tracked entry in
    /// one reflection pass.
    /// </summary>
    private static void CopyOwnedNavigations<TEntity>(EntityEntry<TEntity> trackedEntry, TEntity source)
        where TEntity : class
    {
        foreach (var nav in trackedEntry.Navigations
                     .Where(n => n.Metadata.TargetEntityType.IsOwned()))
        {
            var propInfo = typeof(TEntity).GetProperty(nav.Metadata.Name);
            if (propInfo != null)
                nav.CurrentValue = propInfo.GetValue(source);
        }
    }
}

/// <summary>What <see cref="EfProjectionWriter.LoadFoldSaveAsync{TEntity}"/> folded, and whether it saved it.</summary>
internal readonly record struct EfFoldOutcome<TEntity>(
    IReadOnlyDictionary<string, TEntity> Upserts,
    IReadOnlyCollection<string> Deletes,
    bool Saved)
{
    internal bool NothingToWrite => Upserts.Count == 0 && Deletes.Count == 0;
}
