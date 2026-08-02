using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="IProjectionRebuildStore"/> implementations.
///
/// Derive from this class and implement <see cref="CreateStore"/> to run Alberto's own
/// rebuild-store test suite against your implementation. Every fact describes an observable
/// contract that all implementations must satisfy.
///
/// <para>
/// Several facts exercise coordinator-only transitions by casting the returned store to
/// <c>IProjectionRebuildCoordinatorStore</c>. Both adapters ship as a single class that
/// implements both interfaces, so the cast succeeds. When a future adapter intentionally
/// separates them, override <see cref="SupportsCoordinatorOperations"/> to <see langword="false"/>
/// and those facts will be skipped automatically.
/// </para>
/// </summary>
public abstract class ProjectionRebuildStoreSpecification
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Unique processor ID generated per test instance for isolation across concurrent runs.
    /// </summary>
    protected string ProcessorId { get; } = $"test-processor-{Guid.NewGuid():N}";

    /// <summary>
    /// Stable projection type paired with <see cref="ProcessorId"/>. The two are different
    /// strings (as they are in production) to make sure the store does not confuse them.
    /// </summary>
    protected string ProjectionType { get; } = $"test-projection-{Guid.NewGuid():N}";

    /// <summary>
    /// Factory method called once per fact. Return a fresh store for each call; the spec
    /// requires each fact to receive its own isolated instance.
    /// </summary>
    protected abstract Task<IProjectionRebuildStore> CreateStore();

    // ── Capability hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the returned store also implements the internal coordinator interface and
    /// supports <c>MarkReady</c>, <c>CompletePromotion</c>, <c>CompleteAbort</c>, and
    /// <c>DiscardStateVersion</c>. Both shipped adapters return <see langword="true"/>.
    /// </summary>
    protected virtual bool SupportsCoordinatorOperations => true;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Casts the store to <c>IProjectionRebuildCoordinatorStore</c>. Only called from facts
    /// that first verify <see cref="SupportsCoordinatorOperations"/>.
    /// </summary>
    private static IProjectionRebuildCoordinatorStore AsCoordinator(IProjectionRebuildStore store) =>
        (IProjectionRebuildCoordinatorStore)store;

    // ── GetAsync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A processor that has never been rebuilt must report as
    /// <see cref="RebuildStatus.Idle"/> at active version 1, rather than as a missing
    /// value — callers have exactly one shape to handle.
    /// </summary>
    [Fact]
    public async Task Get_UnknownProcessor_ReturnsIdleAtVersionOne()
    {
        var store = await CreateStore();

        var state = await store.GetAsync(ProcessorId, ProjectionType, Ct);

        state.Status.Should().Be(RebuildStatus.Idle);
        state.ActiveVersion.Should().Be(1);
        state.RebuildingVersion.Should().BeNull();
        state.IsRebuildInFlight.Should().BeFalse();
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ListAsync</c> must return only processors that have a recorded row; an empty
    /// store must return an empty list.
    /// </summary>
    [Fact]
    public async Task List_EmptyStore_ReturnsEmpty()
    {
        var store = await CreateStore();

        var list = await store.ListAsync(Ct);

        list.Should().NotContain(s => s.ProcessorId == ProcessorId);
    }

    /// <summary>
    /// <c>ListAsync</c> must include a processor after its first rebuild is started.
    /// </summary>
    [Fact]
    public async Task List_AfterStart_IncludesProcessor()
    {
        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var list = await store.ListAsync(Ct);

        list.Should().ContainSingle(s => s.ProcessorId == ProcessorId);
    }

    // ── StartAsync ────────────────────────────────────────────────────────────

    /// <summary>
    /// The first rebuild must move the processor to
    /// <see cref="RebuildStatus.Rebuilding"/>, set <c>RebuildingVersion</c> to 2, and
    /// record the target position.
    /// </summary>
    [Fact]
    public async Task Start_FirstRebuild_MovesToRebuildingAtVersionTwo()
    {
        var store = await CreateStore();

        var state = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 42, Ct);

        state.Status.Should().Be(RebuildStatus.Rebuilding);
        state.RebuildingVersion.Should().Be(2);
        state.ActiveVersion.Should().Be(1);
        state.TargetPosition.Should().Be(42);
        state.IsRebuildInFlight.Should().BeTrue();
    }

    /// <summary>
    /// Calling <c>StartAsync</c> while a rebuild is already in flight must throw
    /// <see cref="RebuildStateException"/>.
    /// </summary>
    [Fact]
    public async Task Start_WhileInFlight_ThrowsRebuildStateException()
    {
        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var act = () => store.StartAsync(ProcessorId, ProjectionType, targetPosition: 200, Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    /// <summary>
    /// A rebuild started after an aborted one must receive a version number higher than
    /// the aborted version, not <c>ActiveVersion + 1</c>. Reusing the aborted version
    /// would let a shadow loop that has not yet learned of the abort seed the fresh replay
    /// with its own leftovers, applying every event twice.
    /// </summary>
    [Fact]
    public async Task Start_AfterAbort_AllocatesNewVersionNotAbortedVersion()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations to complete the abort.");

        var store = await CreateStore();

        // Start and immediately abort.
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);
        await AsCoordinator(store).CompleteAbortAsync(ProcessorId, Ct);

        // Start a second rebuild.
        var second = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 200, Ct);

        // The aborted rebuild used version 2. The next must be 3, not 2.
        second.RebuildingVersion.Should().Be(3);
        second.LastAllocatedVersion.Should().Be(3);
    }

    // ── RequestPromotion / RequestAbort ───────────────────────────────────────

    /// <summary>
    /// <c>RequestPromotionAsync</c> must record <see cref="RebuildOperatorAction.Promote"/>
    /// intent on a processor with a rebuild in flight.
    /// </summary>
    [Fact]
    public async Task RequestPromotion_WhileInFlight_RecordsPromoteIntent()
    {
        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var state = await store.RequestPromotionAsync(ProcessorId, force: false, Ct);

        state.RequestedAction.Should().Be(RebuildOperatorAction.Promote);
    }

    /// <summary>
    /// <c>RequestPromotionAsync</c> with <c>force: true</c> must record
    /// <see cref="RebuildOperatorAction.ForcePromote"/>.
    /// </summary>
    [Fact]
    public async Task RequestPromotion_Force_RecordsForcePromoteIntent()
    {
        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var state = await store.RequestPromotionAsync(ProcessorId, force: true, Ct);

        state.RequestedAction.Should().Be(RebuildOperatorAction.ForcePromote);
    }

    /// <summary>
    /// <c>RequestAbortAsync</c> must record <see cref="RebuildOperatorAction.Abort"/>
    /// intent on a processor with a rebuild in flight.
    /// </summary>
    [Fact]
    public async Task RequestAbort_WhileInFlight_RecordsAbortIntent()
    {
        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var state = await store.RequestAbortAsync(ProcessorId, Ct);

        state.RequestedAction.Should().Be(RebuildOperatorAction.Abort);
    }

    /// <summary>
    /// <c>RequestPromotionAsync</c> when no rebuild is in flight must throw
    /// <see cref="RebuildStateException"/>.
    /// </summary>
    [Fact]
    public async Task RequestPromotion_NotInFlight_ThrowsRebuildStateException()
    {
        var store = await CreateStore();

        var act = () => store.RequestPromotionAsync(ProcessorId, ct: Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    /// <summary>
    /// <c>RequestAbortAsync</c> when no rebuild is in flight must throw
    /// <see cref="RebuildStateException"/>.
    /// </summary>
    [Fact]
    public async Task RequestAbort_NotInFlight_ThrowsRebuildStateException()
    {
        var store = await CreateStore();

        var act = () => store.RequestAbortAsync(ProcessorId, Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    // ── Coordinator: MarkReady ────────────────────────────────────────────────

    /// <summary>
    /// <c>MarkReadyAsync</c> must move the processor from
    /// <see cref="RebuildStatus.Rebuilding"/> to <see cref="RebuildStatus.Ready"/>
    /// without changing the version numbers.
    /// </summary>
    [Fact]
    public async Task MarkReady_WhileRebuilding_MovesToReady()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var started = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var state = await AsCoordinator(store).MarkReadyAsync(ProcessorId, Ct);

        state.Status.Should().Be(RebuildStatus.Ready);
        state.ActiveVersion.Should().Be(started.ActiveVersion);
        state.RebuildingVersion.Should().Be(started.RebuildingVersion);
        state.IsRebuildInFlight.Should().BeTrue();
    }

    /// <summary>
    /// <c>MarkReadyAsync</c> when no rebuild is in flight must throw
    /// <see cref="RebuildStateException"/>.
    /// </summary>
    [Fact]
    public async Task MarkReady_NotInFlight_ThrowsRebuildStateException()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();

        var act = () => AsCoordinator(store).MarkReadyAsync(ProcessorId, Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    // ── Coordinator: CompletePromotion ────────────────────────────────────────

    /// <summary>
    /// <c>CompletePromotionAsync</c> must flip the rebuilding version to active, return the
    /// previously active version as <see cref="RebuildOutcome.DiscardedVersion"/>, and move
    /// the status to <see cref="RebuildStatus.Completed"/>.
    /// </summary>
    [Fact]
    public async Task CompletePromotion_FromReady_FlipsVersionAndReturnsDiscarded()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);
        var started = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);
        await coordinator.MarkReadyAsync(ProcessorId, Ct);

        var outcome = await coordinator.CompletePromotionAsync(ProcessorId, force: false, Ct);

        outcome.State.Status.Should().Be(RebuildStatus.Completed);
        outcome.State.ActiveVersion.Should().Be(started.RebuildingVersion!.Value);
        outcome.State.RebuildingVersion.Should().BeNull();
        outcome.State.IsRebuildInFlight.Should().BeFalse();
        outcome.DiscardedVersion.Should().Be(started.ActiveVersion);
    }

    /// <summary>
    /// <c>CompletePromotionAsync</c> without <c>force</c> on a processor still in
    /// <see cref="RebuildStatus.Rebuilding"/> must throw
    /// <see cref="RebuildStateException"/> — the rebuild has not finished replaying.
    /// </summary>
    [Fact]
    public async Task CompletePromotion_WhileRebuilding_WithoutForce_ThrowsRebuildStateException()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var act = () => AsCoordinator(store).CompletePromotionAsync(ProcessorId, force: false, Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    /// <summary>
    /// <c>CompletePromotionAsync</c> with <c>force: true</c> on a processor still in
    /// <see cref="RebuildStatus.Rebuilding"/> must succeed and flip the version.
    /// </summary>
    [Fact]
    public async Task CompletePromotion_WhileRebuilding_WithForce_Succeeds()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);
        var started = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var outcome = await coordinator.CompletePromotionAsync(ProcessorId, force: true, Ct);

        outcome.State.Status.Should().Be(RebuildStatus.Completed);
        outcome.State.ActiveVersion.Should().Be(started.RebuildingVersion!.Value);
        outcome.DiscardedVersion.Should().Be(started.ActiveVersion);
    }

    // ── Coordinator: CompleteAbort ─────────────────────────────────────────────

    /// <summary>
    /// <c>CompleteAbortAsync</c> must return the rebuilding version as
    /// <see cref="RebuildOutcome.DiscardedVersion"/>, clear <c>RebuildingVersion</c>,
    /// leave <c>ActiveVersion</c> unchanged, and move the status to
    /// <see cref="RebuildStatus.Aborted"/>.
    /// </summary>
    [Fact]
    public async Task CompleteAbort_WhileInFlight_ReturnsAbandonedVersionAndLeavesActiveUnchanged()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);
        var started = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);

        var outcome = await coordinator.CompleteAbortAsync(ProcessorId, Ct);

        outcome.State.Status.Should().Be(RebuildStatus.Aborted);
        outcome.State.ActiveVersion.Should().Be(started.ActiveVersion);
        outcome.State.RebuildingVersion.Should().BeNull();
        outcome.State.IsRebuildInFlight.Should().BeFalse();
        outcome.DiscardedVersion.Should().Be(started.RebuildingVersion!.Value);
    }

    /// <summary>
    /// <c>CompleteAbortAsync</c> when no rebuild is in flight must throw
    /// <see cref="RebuildStateException"/>.
    /// </summary>
    [Fact]
    public async Task CompleteAbort_NotInFlight_ThrowsRebuildStateException()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();

        var act = () => AsCoordinator(store).CompleteAbortAsync(ProcessorId, Ct);

        await act.Should().ThrowAsync<RebuildStateException>();
    }

    // ── Coordinator: DiscardStateVersionAsync ─────────────────────────────────

    /// <summary>
    /// <c>DiscardStateVersionAsync</c> must be callable for the discarded version returned
    /// by <c>CompletePromotionAsync</c> and must not throw. Reclaiming a version with no
    /// rows is a no-op by contract, so this is safe regardless of whether the store holds
    /// any actual state rows for that version.
    /// </summary>
    [Fact]
    public async Task DiscardStateVersion_AfterPromotion_DoesNotThrow()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);
        await coordinator.MarkReadyAsync(ProcessorId, Ct);
        var outcome = await coordinator.CompletePromotionAsync(ProcessorId, force: false, Ct);

        // The superseded version's rows outlive the promotion transition by design — a reader
        // that resolved the old version number before the flip must not find empty state.
        // DiscardStateVersionAsync is what the coordinator's sweep calls after the grace period.
        var act = () => coordinator.DiscardStateVersionAsync(
            ProjectionType, outcome.DiscardedVersion, Ct);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// <c>DiscardStateVersionAsync</c> must be callable for the discarded version returned
    /// by <c>CompleteAbortAsync</c> and must not throw.
    /// </summary>
    [Fact]
    public async Task DiscardStateVersion_AfterAbort_DoesNotThrow()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);
        await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);
        var outcome = await coordinator.CompleteAbortAsync(ProcessorId, Ct);

        // The shadow loop only learns of the abort on its next poll, so its last writes
        // may arrive after the transition. The sweep reclaims the abandoned version after
        // the grace period rather than racing those writes.
        var act = () => coordinator.DiscardStateVersionAsync(
            ProjectionType, outcome.DiscardedVersion, Ct);

        await act.Should().NotThrowAsync();
    }

    // ── Lifecycle round-trip ──────────────────────────────────────────────────

    /// <summary>
    /// After a completed promotion, a new rebuild can be started for the same processor.
    /// The new rebuilding version must be higher than any previously allocated one.
    /// </summary>
    [Fact]
    public async Task Start_AfterCompletedPromotion_AllocatesHigherVersion()
    {
        if (!SupportsCoordinatorOperations)
            Assert.Skip("Requires coordinator operations.");

        var store = await CreateStore();
        var coordinator = AsCoordinator(store);

        var first = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 100, Ct);
        await coordinator.MarkReadyAsync(ProcessorId, Ct);
        await coordinator.CompletePromotionAsync(ProcessorId, force: false, Ct);

        var second = await store.StartAsync(ProcessorId, ProjectionType, targetPosition: 200, Ct);

        // After the first promotion: active_version = 2, last_allocated_version = 2.
        // The second rebuild must get version 3.
        second.RebuildingVersion.Should().BeGreaterThan(first.RebuildingVersion!.Value);
    }
}
