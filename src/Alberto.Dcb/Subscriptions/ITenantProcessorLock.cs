namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Distributed lock for tenant-level processor leadership.
/// Allows multiple instances to each claim different tenants for processing.
/// </summary>
public interface ITenantProcessorLock
{
    /// <summary>
    /// Tries to acquire leadership for a specific tenant within a consumer group.
    /// Returns a disposable lease if acquired, null if another instance owns the tenant.
    /// The lock is held as long as the lease is not disposed.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="tenantId">The tenant ID to acquire the lock for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable lease if acquired, null if another instance owns the tenant.</returns>
    Task<IAsyncDisposable?> TryAcquireForTenantAsync(
        string consumerId, string tenantId, CancellationToken ct = default);
}
