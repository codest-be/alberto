using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Wraps an ICheckpointStore with in-memory buffering.
/// Flushes to underlying store every flushInterval or on disposal.
/// On crash: Some checkpoints may be lost, causing event reprocessing.
/// Processors must be idempotent.
/// </summary>
internal sealed class BufferedCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly ICheckpointStore _innerStore;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger<BufferedCheckpointStore>? _logger;
    private readonly ConcurrentDictionary<string, long> _pendingCheckpoints = new();
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a buffered checkpoint store.
    /// </summary>
    /// <param name="innerStore">The underlying checkpoint store to flush to.</param>
    /// <param name="flushInterval">How often to flush. Defaults to 5 seconds.</param>
    /// <param name="logger">Optional logger for error reporting.</param>
    public BufferedCheckpointStore(
        ICheckpointStore innerStore,
        TimeSpan? flushInterval = null,
        ILogger<BufferedCheckpointStore>? logger = null)
    {
        _innerStore = innerStore ?? throw new ArgumentNullException(nameof(innerStore));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        _logger = logger;
        _flushTimer = new Timer(OnFlushTimer, null, _flushInterval, _flushInterval);
    }

    private async void OnFlushTimer(object? state)
    {
        if (_disposed) return;
        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to flush buffered checkpoints to underlying store");
        }
    }

    /// <inheritdoc/>
    public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        // Check buffer first, then underlying store
        if (_pendingCheckpoints.TryGetValue(processorId, out var buffered))
            return Task.FromResult<long?>(buffered);
        return _innerStore.GetAsync(processorId, ct);
    }

    /// <inheritdoc/>
    public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
    {
        // Just buffer it - will be flushed by timer
        _pendingCheckpoints[processorId] = position;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        // Remove from buffer and reset in underlying store
        _pendingCheckpoints.TryRemove(processorId, out _);
        await _innerStore.ResetAsync(processorId, ct);
    }

    /// <summary>
    /// Flushes all pending checkpoints to the underlying store.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (!await _flushLock.WaitAsync(0, ct)) return; // Skip if flush in progress

        try
        {
            // Take snapshot of keys to process
            var keys = _pendingCheckpoints.Keys.ToList();
            foreach (var processorId in keys)
            {
                if (_pendingCheckpoints.TryRemove(processorId, out var position))
                {
                    await _innerStore.SaveAsync(processorId, position, ct);
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();

        // Final flush on shutdown - don't skip if another flush is in progress
        await _flushLock.WaitAsync();
        try
        {
            var keys = _pendingCheckpoints.Keys.ToList();
            foreach (var processorId in keys)
            {
                if (_pendingCheckpoints.TryRemove(processorId, out var position))
                {
                    await _innerStore.SaveAsync(processorId, position);
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }

        _flushLock.Dispose();
    }
}
