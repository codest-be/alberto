using System.Collections.Concurrent;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Checkpoint store that throttles database writes by batching updates.
/// Updates are cached in-memory and periodically flushed to the underlying store.
/// This significantly reduces database load during high-throughput scenarios.
/// </summary>
public sealed class CachingCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly ICheckpointStore _inner;
    private readonly TimeSpan _flushInterval;
    private readonly ConcurrentDictionary<string, long?> _cache = new();
    private readonly ConcurrentDictionary<string, long> _dirty = new();
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new caching checkpoint store.
    /// </summary>
    /// <param name="inner">The underlying checkpoint store.</param>
    /// <param name="flushInterval">How often to flush dirty checkpoints to the database. Default is 1 second.</param>
    public CachingCheckpointStore(ICheckpointStore inner, TimeSpan? flushInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(1);
        _flushTimer = new Timer(OnFlushTimer, null, _flushInterval, _flushInterval);
    }

    public async Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        // Return cached value if we have it
        if (_cache.TryGetValue(processorId, out var cached))
        {
            return cached;
        }

        // Load from store and cache
        var position = await _inner.GetAsync(processorId, ct);
        _cache[processorId] = position;
        return position;
    }

    public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
    {
        // Update cache immediately
        _cache[processorId] = position;

        // Mark as dirty for later flush
        _dirty[processorId] = position;

        // Don't write to DB immediately - the timer will flush periodically
        return Task.CompletedTask;
    }

    public async Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        // Clear from cache and dirty set
        _cache.TryRemove(processorId, out _);
        _dirty.TryRemove(processorId, out _);

        // Reset in underlying store immediately
        await _inner.ResetAsync(processorId, ct);
    }

    private async void OnFlushTimer(object? state)
    {
        if (_disposed) return;

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch
        {
            // Log but don't throw from timer callback
        }
    }

    /// <summary>
    /// Flushes all dirty checkpoints to the underlying store.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_dirty.IsEmpty) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            // Snapshot and clear dirty set
            var toFlush = _dirty.ToArray();
            foreach (var kvp in toFlush)
            {
                _dirty.TryRemove(kvp.Key, out _);
            }

            // Write all dirty checkpoints
            foreach (var (processorId, position) in toFlush)
            {
                await _inner.SaveAsync(processorId, position, ct);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();

        // Flush any remaining dirty checkpoints
        await FlushAsync(CancellationToken.None);

        _flushLock.Dispose();
    }
}
