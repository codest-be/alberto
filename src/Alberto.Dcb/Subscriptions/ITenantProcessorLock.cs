namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Distributed lease management for tenant-level processor leadership.
/// Uses database-backed leases that expire and must be renewed.
/// Allows multiple instances to each claim different tenants for processing.
/// </summary>
public interface ITenantProcessorLock
{
    /// <summary>
    /// Tries to acquire a lease for a specific tenant within a consumer group.
    /// Returns a lease if acquired, null if another instance owns the tenant.
    /// The lease must be periodically renewed via <see cref="RenewLeasesAsync"/>.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="tenantId">The tenant ID to acquire the lease for.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lease if acquired, null if another instance owns the tenant.</returns>
    Task<ITenantLease?> TryAcquireForTenantAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Renews all leases owned by the specified replica.
    /// Should be called periodically (e.g., every leaseDuration / 2).
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant IDs whose leases were renewed.</returns>
    Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Releases a specific lease. Called during graceful shutdown.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="tenantId">The tenant ID to release.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseLeaseAsync(
        string consumerId, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Releases a specific lease owned by the specified replica.
    /// Only releases if the lease is actually owned by this replica.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="tenantId">The tenant ID to release.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseTenantLeaseAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Releases all leases owned by the specified replica. Called during graceful shutdown.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Discovers all known tenant IDs from the event store.
    /// Used for upfront tenant distribution at startup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of all tenant IDs that have produced events.</returns>
    Task<IReadOnlyList<string>> GetKnownTenantsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the lease duration configured for this lock.
    /// Used by the consumer to determine renewal interval.
    /// </summary>
    TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Gets all active leases for a consumer group.
    /// Used for monitoring and deployment orchestration.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All active leases for the consumer group.</returns>
    Task<IReadOnlyList<TenantLeaseInfo>> GetAllLeasesAsync(
        string consumerId, CancellationToken ct = default);
}

/// <summary>
/// Information about an active tenant lease.
/// </summary>
/// <param name="TenantId">The tenant ID this lease is for.</param>
/// <param name="ReplicaId">The replica instance holding this lease.</param>
/// <param name="AcquiredAt">When the lease was originally acquired.</param>
/// <param name="ExpiresAt">When the lease will expire if not renewed.</param>
public record TenantLeaseInfo(
    string TenantId,
    string ReplicaId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);
