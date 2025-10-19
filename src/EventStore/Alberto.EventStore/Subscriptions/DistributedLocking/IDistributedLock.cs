namespace Alberto.EventStore.Subscriptions.DistributedLocking;

/// <summary>
/// Represents a distributed lock for coordinating polling across multiple service instances.
/// Ensures only one instance actively polls the event store at a time.
/// </summary>
public interface IDistributedLock : IAsyncDisposable
{
    /// <summary>
    /// Gets whether this instance currently holds the lock.
    /// </summary>
    bool IsLockHeld { get; }

    /// <summary>
    /// Attempts to acquire the distributed lock.
    /// Returns true if the lock was successfully acquired, false otherwise.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lock acquired, false otherwise</returns>
    ValueTask<bool> TryAcquireLockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the distributed lock if held.
    /// Safe to call multiple times or when lock is not held.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask ReleaseLockAsync(CancellationToken cancellationToken = default);
}