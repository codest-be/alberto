using Alberto.EventStore.Subscriptions.DistributedLocking;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.InMemory.Subscriptions.DistributedLocking;

/// <summary>
/// In-memory implementation of distributed lock for testing and single-instance scenarios.
/// Uses SemaphoreSlim for in-process locking.
/// </summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ILogger<InMemoryDistributedLock> _logger;
    private readonly string _moduleKey;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;
    private bool _isLockHeld;

    public InMemoryDistributedLock(
        string moduleKey,
        ILogger<InMemoryDistributedLock> logger)
    {
        _moduleKey = moduleKey;
        _logger = logger;
        _semaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets whether this instance currently holds the lock.
    /// </summary>
    public bool IsLockHeld => _isLockHeld && !_disposed;

    /// <summary>
    /// Attempts to acquire the distributed lock.
    /// </summary>
    public async ValueTask<bool> TryAcquireLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isLockHeld)
        {
            _logger.LogDebug("Lock already held for module '{ModuleKey}'", _moduleKey);
            return true;
        }

        try
        {
            // Try to acquire without blocking (timeout = 0)
            var acquired = await _semaphore.WaitAsync(0, cancellationToken);

            if (acquired)
            {
                _isLockHeld = true;
                _logger.LogInformation(
                    "Acquired distributed lock for module '{ModuleKey}'. This instance is now the polling leader.",
                    _moduleKey
                );
                return true;
            }

            _logger.LogDebug(
                "Failed to acquire distributed lock for module '{ModuleKey}'. Another instance is the leader.",
                _moduleKey
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring distributed lock for module '{ModuleKey}'", _moduleKey);
            return false;
        }
    }

    /// <summary>
    /// Releases the distributed lock if held.
    /// </summary>
    public ValueTask ReleaseLockAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLockHeld)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            _semaphore.Release();
            _isLockHeld = false;
            _logger.LogInformation("Released distributed lock for module '{ModuleKey}'", _moduleKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error releasing distributed lock for module '{ModuleKey}'", _moduleKey);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the lock and releases it if held.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ReleaseLockAsync();
        _semaphore.Dispose();
    }
}