namespace Alberto.Subscriptions;

/// <summary>
/// Extends <see cref="ICheckpointStore"/> with lease-fenced writes.
/// Prevents zombie consumers from advancing the checkpoint after their lease expires or
/// from writing with a stale fence token from a previous ownership stretch.
/// </summary>
public interface IFencedCheckpointStore : ICheckpointStore
{
    /// <summary>
    /// Saves the checkpoint for <paramref name="processorId"/> only if two guards pass atomically:
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Guard 1 — Lease check</b>: the lease table must show an active (non-expired) lease held
    /// by <paramref name="replicaId"/> with <paramref name="fenceToken"/> as the current generation
    /// (when <paramref name="useProcessorLeaseFencing"/> is <see langword="true"/>).
    /// A false return from this guard means the caller has lost the lease and must stop processing.
    /// </para>
    /// <para>
    /// <b>Guard 2 — Checkpoint-row guard</b>: even when Guard 1 passes, the write is silently
    /// discarded if the checkpoint row already carries a fence token higher than
    /// <paramref name="fenceToken"/>. This is defence in depth: it prevents a superseded
    /// generation from overwriting progress a later generation has already recorded, even if
    /// the lease check were somehow bypassed (e.g. via a stale in-process cache).
    /// </para>
    /// <para>
    /// <b>Ordering</b>: the position is written with GREATEST semantics — a position lower than
    /// the current stored checkpoint is silently discarded, matching
    /// <see cref="ICheckpointStore.SaveAsync"/>. A false return therefore does not indicate that
    /// the position was persisted; the stored checkpoint is unchanged by a rejected write.
    /// </para>
    /// <para>
    /// <b>Atomicity</b>: both guards and the checkpoint update execute atomically. A concurrent
    /// write from another replica observes either the full effect of this write or none of it.
    /// </para>
    /// <para>
    /// <b>Error modes</b>: returns <see langword="false"/> when the lease is expired, the replica
    /// identity does not match, the fence token does not match the current generation, or the
    /// checkpoint row has a higher fence token. Returns <see langword="true"/> when the write was
    /// accepted (position may still be discarded by GREATEST if it is not the highest seen).
    /// Throws on transient infrastructure failures (e.g. database connectivity); callers should
    /// propagate such exceptions rather than treating them as false returns.
    /// </para>
    /// </remarks>
    /// <param name="processorId">The processor whose checkpoint to save.</param>
    /// <param name="position">The position to save.</param>
    /// <param name="consumerId">The consumer (module) identity.</param>
    /// <param name="replicaId">The replica identity to verify against the lease.</param>
    /// <param name="fenceToken">
    /// The <see cref="IProcessorLease.FenceToken"/> of the lease the caller believes it holds.
    /// The write is rejected unless this is the generation that currently owns the processor and
    /// no later generation has already written to the checkpoint row. Ignored when
    /// <paramref name="useProcessorLeaseFencing"/> is <see langword="false"/>: tenant leases have
    /// no single owner per processor and therefore no generation token to present.
    /// </param>
    /// <param name="useProcessorLeaseFencing">
    /// When <see langword="true"/>, validates against the processor-lease table
    /// (<c>alberto_processor_leases</c>) using Guard 1 and Guard 2.
    /// When <see langword="false"/>, validates against the tenant-lease table
    /// (<c>alberto_tenant_leases</c>); <paramref name="fenceToken"/> is ignored.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> if both guards passed and the write was accepted (position may
    /// still have been discarded by GREATEST); <see langword="false"/> if either guard rejected
    /// the write. A false return is the signal for the caller to stop processing.
    /// </returns>
    Task<bool> SaveIfLeaseHeldAsync(
        string processorId,
        long position,
        string consumerId,
        string replicaId,
        long fenceToken,
        bool useProcessorLeaseFencing = false,
        CancellationToken ct = default);
}

/// <summary>
/// Client-facing interface for a checkpoint store that can be configured with
/// lease-fencing parameters and that supports multiple fence-violation subscribers.
/// Decouples callers from the concrete <see cref="CachingCheckpointStore"/> type so
/// that wrapping the store cannot silently drop fencing.
/// </summary>
/// <remarks>
/// <para>
/// Fence-violation handlers are registered additively via
/// <see cref="SubscribeFenceViolation"/> rather than through a single settable property.
/// This allows multiple callers (e.g. a per-loop cancellation registered by
/// <see cref="ControlLoopAssembler"/> and a group-stop registered by the lease-aware group)
/// to coexist without trampling each other. Subscriptions are process-lifetime; there is no
/// unsubscribe — the store and its callers share the same lifetime.
/// </para>
/// </remarks>
internal interface IFencableCheckpointStore
{
    /// <summary>
    /// Provides the fencing context so that periodic flushes use lease-fenced writes
    /// when the underlying store implements <see cref="IFencedCheckpointStore"/>.
    /// </summary>
    void SetFencingContext(FencingContext ctx);

    /// <summary>
    /// Adds <paramref name="handler"/> to the set of callbacks invoked when a fenced
    /// checkpoint write is rejected because the lease has expired.
    /// The handler receives the processor ID of the fenced processor.
    /// Multiple handlers may be registered; all are called on each violation.
    /// </summary>
    void SubscribeFenceViolation(Action<string> handler);
}
