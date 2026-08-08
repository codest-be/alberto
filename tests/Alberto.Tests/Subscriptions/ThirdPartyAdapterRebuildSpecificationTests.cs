using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;

namespace Alberto.Tests.Subscriptions;

// ── Simulation of third-party adapter ────────────────────────────────────────
//
// MinimalRebuildStore implements only the public IProjectionRebuildStore.
// It does NOT implement IProjectionRebuildCoordinatorStore (internal to Alberto).
// This models a third-party adapter author who can only see the public interface.
//
// The defect that existed before this fix: AsCoordinator cast the store to the
// internal interface and threw InvalidCastException at runtime:
//
//   System.InvalidCastException: Unable to cast object of type 'MinimalRebuildStore'
//   to type 'Alberto.Subscriptions.IProjectionRebuildCoordinatorStore'.
//
// The failure is a runtime cast, not a visibility issue.  Alberto.Tests has
// InternalsVisibleTo access to Alberto (it can reference the internal interface),
// but MinimalRebuildStore deliberately does not implement it — exactly as a
// third-party adapter's class cannot, because the interface is not in their
// compilation unit.  The InvalidCastException does not care which assembly hosts
// the test; it only checks whether the object's type implements the interface.
//
// After the fix the spec asks the subclass for a RebuildCoordinatorFacade instead
// of casting.  ThirdPartyAdapterSpecificationTests overrides GetCoordinator to
// supply a facade backed by MinimalRebuildStore.Inner, which is the real
// InMemoryProjectionRebuildStore that does own the coordinator-only transitions.

/// <summary>
/// Simulates a third-party IProjectionRebuildStore adapter: wraps
/// InMemoryProjectionRebuildStore but exposes only the public interface.
/// Does NOT implement IProjectionRebuildCoordinatorStore.
/// </summary>
internal sealed class MinimalRebuildStore : IProjectionRebuildStore
{
    internal readonly InMemoryProjectionRebuildStore Inner = new();

    public Task<ProjectionRebuildState> GetAsync(
        string processorId, string projectionType, CancellationToken ct = default) =>
        Inner.GetAsync(processorId, projectionType, ct);

    public Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken ct = default) =>
        Inner.ListAsync(ct);

    public Task<ProjectionRebuildState> StartAsync(
        string processorId, string projectionType, long targetPosition, CancellationToken ct = default) =>
        Inner.StartAsync(processorId, projectionType, targetPosition, ct);

    public Task<ProjectionRebuildState> RequestPromotionAsync(
        string processorId, bool force = false, CancellationToken ct = default) =>
        Inner.RequestPromotionAsync(processorId, force, ct);

    public Task<ProjectionRebuildState> RequestAbortAsync(
        string processorId, CancellationToken ct = default) =>
        Inner.RequestAbortAsync(processorId, ct);
}

/// <summary>
/// Runs the conformance specification against <see cref="MinimalRebuildStore"/>.
/// Overrides <see cref="ProjectionRebuildStoreSpecification.GetCoordinator"/> to
/// bridge coordinator operations via <see cref="MinimalRebuildStore.Inner"/>,
/// exactly as a third-party adapter author would: their wrapper exposes only the
/// public interface, and GetCoordinator supplies a facade backed by the underlying
/// object that owns the coordinator-only transitions.
/// </summary>
public sealed class ThirdPartyAdapterSpecificationTests : ProjectionRebuildStoreSpecification
{
    protected override Task<IProjectionRebuildStore> CreateStore() =>
        Task.FromResult<IProjectionRebuildStore>(new MinimalRebuildStore());

    protected override RebuildCoordinatorFacade GetCoordinator(IProjectionRebuildStore store) =>
        new MinimalCoordinatorFacade(((MinimalRebuildStore)store).Inner);

    // A concrete RebuildCoordinatorFacade that the test assembly provides.
    // Alberto.Tests has InternalsVisibleTo access to Alberto, so it can hold a
    // reference to IProjectionRebuildCoordinatorStore here.  A true third-party
    // author would use their own coordinator object without referencing the
    // internal interface at all.
    private sealed class MinimalCoordinatorFacade : RebuildCoordinatorFacade
    {
        private readonly IProjectionRebuildCoordinatorStore _coordinator;

        internal MinimalCoordinatorFacade(IProjectionRebuildCoordinatorStore coordinator) =>
            _coordinator = coordinator;

        public override Task<ProjectionRebuildState> MarkReadyAsync(
            string processorId, CancellationToken ct = default) =>
            _coordinator.MarkReadyAsync(processorId, ct);

        public override Task<RebuildOutcome> CompletePromotionAsync(
            string processorId, bool force = false, CancellationToken ct = default) =>
            _coordinator.CompletePromotionAsync(processorId, force, ct);

        public override Task<RebuildOutcome> CompleteAbortAsync(
            string processorId, CancellationToken ct = default) =>
            _coordinator.CompleteAbortAsync(processorId, ct);

        public override Task DiscardStateVersionAsync(
            string projectionType, int version, CancellationToken ct = default) =>
            _coordinator.DiscardStateVersionAsync(projectionType, version, ct);
    }
}
