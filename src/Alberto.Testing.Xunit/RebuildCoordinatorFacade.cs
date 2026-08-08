using Alberto.Subscriptions;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Adapter base that lets a <see cref="ProjectionRebuildStoreSpecification"/> subclass
/// supply coordinator operations without depending on the internal
/// <c>IProjectionRebuildCoordinatorStore</c> interface.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="ProjectionRebuildStoreSpecification"/> subclass must override
/// <see cref="ProjectionRebuildStoreSpecification.GetCoordinator"/> to return an instance
/// of this class. The shipped adapters — <c>InMemoryProjectionRebuildStore</c> and
/// <c>PostgresProjectionRebuildStore</c> — implement both the public
/// <see cref="IProjectionRebuildStore"/> and the internal coordinator interface on the same
/// class, so their overrides are a single line that casts and wraps in
/// <c>RebuildCoordinatorFacade.FromCoordinatorStore</c>.
/// </para>
/// <para>
/// A third-party adapter that keeps coordinator operations on a separate object subclasses
/// this type directly and delegates to that object, without needing to reference the internal
/// interface at all.
/// </para>
/// </remarks>
public abstract class RebuildCoordinatorFacade
{
    /// <summary>Marks a historical replay ready after its checkpoint reaches the target.</summary>
    /// <param name="processorId">The processor to mark ready.</param>
    /// <param name="ct">Cancellation token.</param>
    public abstract Task<ProjectionRebuildState> MarkReadyAsync(
        string processorId, CancellationToken ct = default);

    /// <summary>
    /// Atomically flips the rebuilt version to active. The caller must already have stopped the
    /// shadow loop and verified checkpoint locality.
    /// </summary>
    /// <param name="processorId">The processor to promote.</param>
    /// <param name="force">Permit promotion before the original target is reached.</param>
    /// <param name="ct">Cancellation token.</param>
    public abstract Task<RebuildOutcome> CompletePromotionAsync(
        string processorId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks an abort complete. The caller must already have stopped the shadow loop.
    /// </summary>
    /// <param name="processorId">The processor to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    public abstract Task<RebuildOutcome> CompleteAbortAsync(
        string processorId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every state row a projection holds at one version, across all tenants.
    /// Reclaiming a version that holds nothing is a no-op.
    /// </summary>
    /// <param name="projectionType">The key the projection's state rows are stored under.</param>
    /// <param name="version">The rebuild version to reclaim.</param>
    /// <param name="ct">Cancellation token.</param>
    public abstract Task DiscardStateVersionAsync(
        string projectionType, int version, CancellationToken ct = default);

    // ── Internal adapter ──────────────────────────────────────────────────────
    //
    // Used by first-party spec overrides inside Alberto.Tests (granted InternalsVisibleTo).
    // Stays internal so external consumers cannot reference IProjectionRebuildCoordinatorStore
    // through this type.

    internal sealed class FromCoordinatorStore : RebuildCoordinatorFacade
    {
        private readonly IProjectionRebuildCoordinatorStore _store;

        internal FromCoordinatorStore(IProjectionRebuildCoordinatorStore store) =>
            _store = store;

        public override Task<ProjectionRebuildState> MarkReadyAsync(
            string processorId, CancellationToken ct = default) =>
            _store.MarkReadyAsync(processorId, ct);

        public override Task<RebuildOutcome> CompletePromotionAsync(
            string processorId, bool force = false, CancellationToken ct = default) =>
            _store.CompletePromotionAsync(processorId, force, ct);

        public override Task<RebuildOutcome> CompleteAbortAsync(
            string processorId, CancellationToken ct = default) =>
            _store.CompleteAbortAsync(processorId, ct);

        public override Task DiscardStateVersionAsync(
            string projectionType, int version, CancellationToken ct = default) =>
            _store.DiscardStateVersionAsync(projectionType, version, ct);
    }
}
