using System.Collections.Concurrent;
using Alberto.Subscriptions;

namespace Alberto.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IStateStore{TState}"/>, for development, tests and
/// samples. State lives in a dictionary and is gone when the process exits.
/// </summary>
/// <remarks>
/// <para>
/// There is no transaction to apply changes under: a projection writing here is not atomic with
/// the checkpoint the way a Postgres or EF projection is, so after a crash it can be replayed and
/// apply an event twice. That is the price of having no database, and the reason this store
/// belongs in tests and samples rather than in production.
/// </para>
/// <para>
/// Like the durable stores, it keys state by rebuild version, so a projection wired for
/// zero-downtime rebuilds behaves the same way here as it does against Postgres.
/// </para>
/// </remarks>
/// <param name="rebuildVersion">
/// The version this store reads and writes. Resolved per operation rather than captured, because
/// promoting a rebuild changes it underneath a long-lived store. Omit it for a projection that is
/// never rebuilt; it then resolves to version 1 forever.
/// </param>
public sealed class InMemoryStateStore<TState>(Func<int>? rebuildVersion = null) : IStateStore<TState>
{
    /// <summary>
    /// A stored document and the write that last touched it. The sequence stands in for the
    /// durable stores' <c>updated_at</c>: a wall clock would tie between two writes in the same
    /// batch, and <see cref="ListRecentAsync"/> would then order them arbitrarily where Postgres
    /// orders them by when the row was actually written.
    /// </summary>
    private readonly record struct Entry(TState State, long Sequence);

    private readonly ConcurrentDictionary<(int Version, string DocumentId), Entry> _documents = new();
    private readonly Func<int> _rebuildVersion = rebuildVersion ?? ProjectionVersions.NeverRebuilt;
    private long _sequence;

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, TState>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        var version = _rebuildVersion();
        var result = new Dictionary<string, TState>();

        foreach (var id in documentIds)
        {
            if (_documents.TryGetValue((version, id), out var entry))
                result[id] = entry.State;
        }

        // Explicit type arg required: Task<T> is not covariant, so Task.FromResult(result)
        // would infer Task<Dictionary<...>> and fail to satisfy the IReadOnlyDictionary contract.
        return Task.FromResult<IReadOnlyDictionary<string, TState>>(result);
    }

    /// <inheritdoc/>
    public Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TState> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);

        var version = _rebuildVersion();

        foreach (var (id, state) in upserts)
            _documents[(version, id)] = new Entry(state, Interlocked.Increment(ref _sequence));

        foreach (var id in deletes)
            _documents.TryRemove((version, id), out _);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TState>> ListRecentAsync(
        int limit = 20,
        CancellationToken ct = default)
    {
        var version = _rebuildVersion();

        IReadOnlyList<TState> result = _documents
            .Where(kvp => kvp.Key.Version == version)
            .OrderByDescending(kvp => kvp.Value.Sequence)
            .Take(limit)
            .Select(kvp => kvp.Value.State)
            .ToList();

        return Task.FromResult(result);
    }
}
