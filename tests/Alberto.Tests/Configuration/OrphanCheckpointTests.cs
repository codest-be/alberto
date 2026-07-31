using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alberto.Tests.Configuration;

public class OrphanCheckpointTests
{
    private sealed class FakeInventory(params string[] processorIds) : ICheckpointInventory
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(processorIds);
        }
    }

    private static AlbertoModuleDefinition Definition(
        OrphanCheckpointPolicy policy,
        params string[] declaredProcessorIds) => new()
    {
        ModuleKey = "orders",
        Checkpoints = new CheckpointOptions { OrphanPolicy = policy },
        Processors =
        [
            .. declaredProcessorIds.Select(id => new ProcessorDeclaration
            {
                ProcessorId = id,
                Kind = ProcessorKind.Reactor,
            }),
        ],
    };

    private static Task RunAsync(
        AlbertoModuleDefinition definition,
        ICheckpointInventory? inventory) =>
        new OrphanCheckpointHostedService(
            definition,
            inventory,
            NullLogger<OrphanCheckpointHostedService>.Instance)
            .StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Strict_fails_startup_when_a_checkpoint_has_no_processor()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary", "OldReactorName"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("OldReactorName");
        exception.Which.Message.Should().Contain("ops checkpoint rename");
    }

    [Fact]
    public async Task Strict_is_silent_when_every_checkpoint_is_claimed()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Warn_does_not_fail_startup()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Warn, "OrderSummary"),
            new FakeInventory("OldReactorName"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Off_does_not_read_the_inventory()
    {
        var inventory = new FakeInventory("OldReactorName");

        var act = () => RunAsync(Definition(OrphanCheckpointPolicy.Off, "OrderSummary"), inventory);

        await act.Should().NotThrowAsync();
        inventory.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_store_that_cannot_enumerate_is_skipped_rather_than_failing()
    {
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            inventory: null);

        await act.Should().NotThrowAsync();
    }

    // -----------------------------------------------------------------------
    // Defence-in-depth: shadow rebuild keys must never trip the orphan check
    // -----------------------------------------------------------------------
    // (b) A stray "{id}::rebuild::{n}" row in the checkpoint store does not
    // brick startup even when the policy is Strict.  The coordinator's sweep
    // is the authoritative cleaner; this filter is the safety net for rows left
    // behind by a crash, an older host, or a shadow loop on a different replica.

    [Fact]
    public async Task Strict_does_not_fail_when_a_shadow_rebuild_checkpoint_is_in_the_store()
    {
        // A completed rebuild left its shadow key behind — exactly what happens when the
        // coordinator crashes between the promotion and the sweep.
        var shadowKey = RebuildableProjection.ShadowProcessorId("OrderSummary", 2);

        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary", shadowKey));

        await act.Should().NotThrowAsync(
            "shadow rebuild keys are artefacts of the rebuild protocol, not missing processors");
    }

    [Fact]
    public async Task Strict_still_reports_real_orphans_alongside_shadow_keys()
    {
        var shadowKey = RebuildableProjection.ShadowProcessorId("OrderSummary", 3);

        // "OldReactorName" is a genuine orphan (handler was renamed).
        // The shadow key must be filtered; the real orphan must still be reported.
        var act = () => RunAsync(
            Definition(OrphanCheckpointPolicy.Strict, "OrderSummary"),
            new FakeInventory("OrderSummary", shadowKey, "OldReactorName"));

        var exception = await act.Should().ThrowAsync<InvalidOperationException>(
            "the real orphan must still be caught even when shadow keys are present");
        exception.Which.Message.Should().Contain("OldReactorName");
        exception.Which.Message.Should().NotContain(shadowKey,
            "shadow keys must be excluded from the orphan report");
    }

    // -----------------------------------------------------------------------
    // Primary fix: coordinator's ClearAsync must reset the shadow checkpoint
    // -----------------------------------------------------------------------
    // (a) After a rebuild version is swept, the shadow checkpoint row is gone.
    // Without this fix the row survives and trips the Strict check on the next
    // restart — even with the defence-in-depth filter above, leaving the row
    // in the store is a latent correctness issue (unbounded growth across many
    // rebuilds) so the authoritative clean-up must happen in the coordinator.

    /// <summary>
    /// A minimal stub that satisfies both <see cref="IProjectionRebuildStore"/> (for
    /// <see cref="ProjectionVersions.RefreshAsync"/>) and
    /// <see cref="IProjectionRebuildCoordinatorStore"/> (for <see cref="RebuildCoordinator"/>).
    /// Only the paths exercised by the startup sweep are implemented.
    /// </summary>
    private sealed class FakeRebuildStore(IReadOnlyList<ProjectionRebuildState> states)
        : IProjectionRebuildStore, IProjectionRebuildCoordinatorStore
    {
        // IProjectionRebuildStore — only ListAsync is called during a sweep
        public Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(states);

        public Task<ProjectionRebuildState> GetAsync(
            string processorId, string projectionType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> StartAsync(
            string processorId, string projectionType, long targetPosition, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> RequestPromotionAsync(
            string processorId, bool force = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> RequestAbortAsync(
            string processorId, CancellationToken ct = default)
            => throw new NotSupportedException();

        // IProjectionRebuildCoordinatorStore — only DiscardStateVersionAsync is called during ClearAsync
        public Task DiscardStateVersionAsync(
            string projectionType, int version, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ProjectionRebuildState> MarkReadyAsync(
            string processorId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RebuildOutcome> CompletePromotionAsync(
            string processorId, bool force = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RebuildOutcome> CompleteAbortAsync(
            string processorId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // -----------------------------------------------------------------------
    // Primary fix: CachingCheckpointStore must surface ICheckpointInventory
    // -----------------------------------------------------------------------
    // On Postgres the checkpoint store is always wrapped in a CachingCheckpointStore.
    // Before the fix the decorator did not implement ICheckpointInventory, so the cast
    // at the DI resolution site yielded null and the hosted service silently skipped the
    // orphan check on every Postgres deployment — the one place where silent orphans
    // actually matter in production.

    /// <summary>
    /// A minimal store that satisfies <see cref="ICheckpointStore"/> but deliberately
    /// opts out of <see cref="ICheckpointInventory"/>. Used to prove that wrapping such
    /// a store in <see cref="CachingCheckpointStore"/> preserves the opt-out rather than
    /// fabricating an empty inventory that looks like a successful all-clear.
    /// </summary>
    private sealed class NoInventoryStore : ICheckpointStore
    {
        private readonly Dictionary<string, long> _data = new();

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(processorId, out var v) ? (long?)v : null);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _data.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CachingDecorator_OrphanCheckSeesCheckpointsThatHaveNotYetBeenFlushed()
    {
        // The caching decorator buffers SaveAsync calls and flushes them on a timer. If
        // ListProcessorIdsAsync delegated directly to the inner store without flushing first,
        // a just-written checkpoint would be absent from the listing — making an active
        // processor look like it has no row and causing the orphan check to miss a genuine
        // rename that only affected that processor. The fix is to flush before listing.
        var ct = TestContext.Current.CancellationToken;
        var inner = new InMemoryCheckpointStore();

        // Long intervals so no background timer fires during the test.
        await using var caching = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromHours(1),
            resyncInterval: TimeSpan.FromHours(1));

        // Write a checkpoint into the buffer only — the inner store is still empty.
        await caching.SaveAsync("OldProcessorName", 100, ct);
        (await inner.GetAsync("OldProcessorName", ct)).Should().BeNull(
            "the checkpoint is still buffered; the inner store must not have it yet");

        // The inventory must flush and then include the buffered entry.
        var inventory = caching.AsInventory;
        inventory.Should().NotBeNull("InMemoryCheckpointStore implements ICheckpointInventory");

        var ids = await inventory!.ListProcessorIdsAsync(ct);
        ids.Should().Contain("OldProcessorName",
            "the decorator must flush pending writes before listing so buffered processors are visible");

        // Feed the inventory to the hosted service under Strict — it must detect the orphan.
        var definition = Definition(OrphanCheckpointPolicy.Strict, "NewProcessorName");
        var act = () => RunAsync(definition, inventory);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>(
            "Strict policy must detect the checkpoint left behind by the renamed processor");
        exception.Which.Message.Should().Contain("OldProcessorName");
    }

    [Fact]
    public async Task CachingDecorator_WrappingNonInventoryStore_DoesNotProduceFalseAllClear()
    {
        // When a custom store opts out of ICheckpointInventory by not implementing it,
        // wrapping it in CachingCheckpointStore must not silently advertise inventory
        // support. Before the fix, CachingCheckpointStore did not implement the interface
        // at all, so the cast at the resolution site returned null and the check was skipped.
        // After the fix, AsInventory returns null for non-inventory inners, preserving
        // the opt-out and preventing a false all-clear under Strict policy.
        var ct = TestContext.Current.CancellationToken;
        var inner = new NoInventoryStore();
        await inner.SaveAsync("OrphanedProcessor", 42, ct);

        await using var caching = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromHours(1),
            resyncInterval: TimeSpan.FromHours(1));

        // The decorator must honour the inner store's opt-out.
        caching.AsInventory.Should().BeNull(
            "a decorator wrapping a non-inventory store must return null from AsInventory, " +
            "not an ICheckpointInventory that would return an empty list and look like an all-clear");

        // Passing null to the hosted service under Strict must skip the check, not pass it.
        // "Skip" is the correct outcome: "we don't know" is better than a false all-clear.
        var definition = Definition(OrphanCheckpointPolicy.Strict, "ActiveProcessor");
        var act = () => RunAsync(definition, inventory: null);

        await act.Should().NotThrowAsync(
            "when the store cannot enumerate, the orphan check is skipped — not passed");
    }

    [Fact]
    public async Task ClearAsync_ResetsTheShadowCheckpoint_SoItDoesNotOrphan()
    {
        var ct = TestContext.Current.CancellationToken;

        // A completed rebuild: version 2 is the active one; version 1 was the previous active
        // and was discarded when the rebuild promoted. The shadow loop that filled version 2
        // left its checkpoint behind at position 42.
        const string processorId = "orders";
        const int discardedVersion = 1;
        var shadowKey = RebuildableProjection.ShadowProcessorId(processorId, discardedVersion);

        var checkpointStore = new InMemoryCheckpointStore();
        await checkpointStore.SaveAsync(shadowKey, 42, ct);

        // The state the database would hold after a successful promotion + elapsed grace period.
        var rebuildState = new ProjectionRebuildState(
            ProcessorId: processorId,
            ProjectionType: processorId,
            ActiveVersion: 2,
            RebuildingVersion: null,
            Status: RebuildStatus.Completed,
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            TargetPosition: null,
            CompletedAt: DateTimeOffset.UtcNow.AddMinutes(-5), // grace period well elapsed
            LastAllocatedVersion: 2);

        var store = new FakeRebuildStore([rebuildState]);

        // ProjectionVersions owns a background refresh loop; dispose it when done.
        await using var versions = new ProjectionVersions(store, refreshInterval: TimeSpan.FromSeconds(30));

        // ReclaimGracePeriod: Zero so the sweep is not held back.
        // PollingInterval: 10s so the main loop does not re-enter before we stop.
        using var coordinator = new RebuildCoordinator(
            projections: [
                new RebuildableProjection(
                    processorId, processorId,
                    _ => throw new InvalidOperationException("no shadow loop should start during a sweep"))
            ],
            rebuildStore: store,
            versions: versions,
            checkpoints: checkpointStore,
            loopFactory: new ShadowControlLoopFactory(
                _ => throw new InvalidOperationException("no shadow loop should start during a sweep")),
            clearers: [],
            options: new RebuildCoordinatorOptions(
                PollingInterval: TimeSpan.FromSeconds(10),
                AutoPromote: false,
                ReclaimGracePeriod: TimeSpan.Zero));

        await coordinator.StartAsync(ct);

        // The startup sweep runs once before entering the main polling loop; all stubs are
        // in-memory, so it completes in microseconds. Poll briefly to let the background task
        // reach that point before asserting.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && await checkpointStore.GetAsync(shadowKey, ct) is not null)
            await Task.Delay(10, ct);

        await coordinator.StopAsync(ct);

        // After the sweep the shadow checkpoint row must be gone. Without the ClearAsync fix
        // it would survive, and the next call to OrphanCheckpointHostedService.StartAsync under
        // a Strict policy would throw — bricking every restart until the row was hand-deleted.
        (await checkpointStore.GetAsync(shadowKey, ct)).Should().BeNull(
            "ClearAsync must call checkpoints.ResetAsync on the shadow key so the row cannot orphan");
    }
}
