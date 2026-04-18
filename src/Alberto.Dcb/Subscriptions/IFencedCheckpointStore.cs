namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Extends ICheckpointStore with lease-fenced writes.
/// Prevents zombie consumers from writing stale checkpoints after their lease expires.
/// </summary>
public interface IFencedCheckpointStore : ICheckpointStore
{
    /// <summary>
    /// Saves checkpoint only if the specified replica still holds an active lease.
    /// Returns false if the lease has expired — the caller should stop processing.
    /// </summary>
    /// <param name="processorId">The processor whose checkpoint to save.</param>
    /// <param name="position">The position to save.</param>
    /// <param name="consumerId">The consumer (module) identity.</param>
    /// <param name="replicaId">The replica identity to verify against the lease.</param>
    /// <param name="useProcessorLeaseFencing">
    /// When true, checks the processor_leases table instead of tenant_leases.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> SaveIfLeaseHeldAsync(
        string processorId,
        long position,
        string consumerId,
        string replicaId,
        bool useProcessorLeaseFencing = false,
        CancellationToken ct = default);
}
