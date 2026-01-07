using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of <see cref="ICheckpointStore"/>.
/// Thread-safe for concurrent access.
/// Useful for testing.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
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
}
