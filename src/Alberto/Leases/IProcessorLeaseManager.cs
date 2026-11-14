using Alberto.Checkpoints;

namespace Alberto.Leases;

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease if acquired, null if another instance owns the processor.</returns>
    Task<IProcessorLease?> TryAcquireAsync(
        string consumerId, string processorId, string replicaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews all leases owned by the specified replica.
    /// Should be called periodically (e.g., every leaseDuration / 3).
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processor IDs whose leases were renewed.</returns>
    Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases all leases owned by the specified replica. Called during graceful shutdown.
    /// </summary>
    /// <param name="consumerId">Unique identifier for the consumer group.</param>
    /// <param name="replicaId">Unique identifier for this replica instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Identifies this acquisition of the lease. Strictly increasing across acquisitions and
    /// unchanged by renewal, so it names one continuous stretch of ownership.
    /// </summary>
    /// <remarks>
    /// Present it on every fenced write (see <c>IFencedCheckpointStore.SaveIfLeaseHeldAsync</c>).
    /// Checking the replica identity alone is not enough: a replica that loses the lease and
    /// later takes it back matches on identity again, so a write it prepared under its earlier
    /// ownership would be accepted and — because the checkpoint upsert is monotonic — could
    /// carry the checkpoint past everything the intervening owner processed.
    /// </remarks>
    long FenceToken { get; }
}

/// <summary>
/// Default implementation of <see cref="IProcessorLease"/>.
/// </summary>
public sealed record ProcessorLease(string ProcessorId, DateTimeOffset ExpiresAt, long FenceToken)
    : IProcessorLease;
