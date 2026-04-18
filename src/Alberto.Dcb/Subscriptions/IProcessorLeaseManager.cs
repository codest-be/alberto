namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Distributed lease management for processor-level leadership.
/// Uses database-backed leases that expire and must be renewed.
/// Allows multiple instances to each claim different processors,
/// ensuring each processor runs on exactly one replica at a time.
/// </summary>
public interface IProcessorLeaseManager
{
    /// <summary>
    /// Tries to acquire a lease for a specific processor within a consumer group.
    /// Returns a lease if acquired, null if another instance owns the processor.
    /// The lease must be periodically renewed via <see cref="RenewLeasesAsync"/>.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group (typically the module key).</param>
    /// <param name="processorId">The processor ID to acquire the lease for.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A lease if acquired, null if another instance owns the processor.</returns>
    Task<IProcessorLease?> TryAcquireAsync(
        string consumerId, string processorId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Renews all leases owned by the specified replica.
    /// Should be called periodically (e.g., every leaseDuration / 3).
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The processor IDs whose leases were renewed.</returns>
    Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Releases all leases owned by the specified replica. Called during graceful shutdown.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active leases for a consumer group. Used for monitoring.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All active leases for the consumer group.</returns>
    Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
        string consumerId, CancellationToken ct = default);

    /// <summary>
    /// Gets the lease duration configured for this manager.
    /// Used by the consumer to determine renewal interval.
    /// </summary>
    TimeSpan LeaseDuration { get; }
}

/// <summary>
/// Represents an acquired processor lease.
/// </summary>
public interface IProcessorLease
{
    string ProcessorId { get; }
    DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// Default implementation of <see cref="IProcessorLease"/>.
/// </summary>
public sealed record ProcessorLease(string ProcessorId, DateTimeOffset ExpiresAt) : IProcessorLease;

/// <summary>
/// Information about an active processor lease, used for monitoring.
/// </summary>
/// <param name="ProcessorId">The processor this lease is for.</param>
/// <param name="ReplicaId">The replica instance holding this lease.</param>
/// <param name="AcquiredAt">When the lease was originally acquired.</param>
/// <param name="ExpiresAt">When the lease will expire if not renewed.</param>
public record ProcessorLeaseInfo(
    string ProcessorId,
    string ReplicaId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);
