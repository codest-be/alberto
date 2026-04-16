using System.Collections.Concurrent;
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.InMemory;

/// <summary>
/// In-memory implementation of dead letter storage.
/// Useful for testing and development.
/// </summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentDictionary<Guid, DeadLetterEntry> _entries = new();

    /// <inheritdoc />
    public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default)
    {
        var entries = _entries.Values
            .Where(e => e.ProcessorId == processorId)
            .OrderByDescending(e => e.FailedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<int> CountAsync(string processorId, CancellationToken ct = default)
    {
        var count = _entries.Values.Count(e => e.ProcessorId == processorId);
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        _entries.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(string processorId, CancellationToken ct = default)
    {
        var toRemove = _entries.Where(e => e.Value.ProcessorId == processorId).Select(e => e.Key).ToList();
        foreach (var id in toRemove)
        {
            _entries.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
    {
        foreach (var entry in _entries.Values.Where(e => e.ProcessorId == processorId))
            _entries[entry.Id] = entry with { RetryRequested = true };

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterEntry>> GetRetryRequestedWithLockAsync(
        string processorId,
        int batchSize = 10,
        CancellationToken ct = default)
    {
        var entries = _entries.Values
            .Where(e => e.ProcessorId == processorId && e.RetryRequested)
            .OrderBy(e => e.FailedAt)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(entries);
    }
}
