namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Extends ICheckpointStore with lease-fenced writes.
/// Prevents zombie consumers from writing stale checkpoints after their lease expires.
/// </summary>
public interface IFencedCheckpointStore : ICheckpointStore
{
    /// <summary>
    /// Saves checkpoint only if the specified replica still holds an active lease.
    /// Returns false if the lease has expired — the caller should stop processing for that tenant.
    /// </summary>
    Task<bool> SaveIfLeaseHeldAsync(
        string processorId,
        long position,
        string consumerId,
        string replicaId,
        CancellationToken ct = default);
}
