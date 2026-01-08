namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Distributed lock for processor leadership.
/// Ensures only one instance processes events at a time.
/// </summary>
public interface IProcessorLock
{
    /// <summary>
    /// Tries to acquire leadership for a consumer.
    /// Returns a disposable lease if acquired, null if another instance is leader.
    /// The lock is held as long as the lease is not disposed.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable lease if acquired, null if another instance is leader.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string consumerId, CancellationToken ct = default);
}
