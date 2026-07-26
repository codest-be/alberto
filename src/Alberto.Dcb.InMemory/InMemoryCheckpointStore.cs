using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="ICheckpointStore"/>.
/// Thread-safe for concurrent access.
/// Useful for testing.
/// </summary>
/// <remarks>
/// <see cref="SaveAsync"/> is monotonic, matching the <c>GREATEST</c> semantics of the
/// PostgreSQL implementation: a backward position is silently discarded.
/// <see cref="RewindAsync"/> is the deliberate escape hatch that can move a checkpoint
/// backwards, mirroring the operator-only rewind path in production.
/// </remarks>
public sealed class InMemoryCheckpointStore : ICheckpointStore, ICheckpointInventory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _checkpoints = new();

    public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_checkpoints.TryGetValue(processorId, out var position)
                ? position
                : (long?)null);
        }
    }

    public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Mirror Postgres GREATEST semantics: SaveAsync is monotonic — a stale flush from a
            // lagging processor cannot roll back a checkpoint that has already moved forward.
            // RewindAsync is the deliberate and only escape hatch for moving backwards.
            if (!_checkpoints.TryGetValue(processorId, out var current) || position > current)
                _checkpoints[processorId] = position;
            return Task.CompletedTask;
        }
    }

    public Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _checkpoints.Remove(processorId);
            return Task.CompletedTask;
        }
    }

    public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _checkpoints[processorId] = position;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Clears all checkpoints. Useful for testing.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _checkpoints.Clear();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_checkpoints.Keys.ToList());
        }
    }
}
