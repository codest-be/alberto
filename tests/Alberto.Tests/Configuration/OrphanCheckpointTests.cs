using Alberto;
using Alberto.Configuration;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alberto.Tests.Configuration;

public class OrphanCheckpointTests
{
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "TestApp";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

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

        // InMemoryCheckpointStore implements ICheckpointInventory and CanEnumerate defaults
        // to true, so the resolution pattern must yield a non-null inventory.
        ICheckpointInventory? inventory =
            caching is ICheckpointInventory { CanEnumerate: true } inv ? inv : null;
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
        // support. CachingCheckpointStore.CanEnumerate returns false when the inner store
        // does not implement ICheckpointInventory, so the resolution-site pattern match
        // (`is ICheckpointInventory { CanEnumerate: true }`) correctly returns null, and
        // the hosted service skips the check rather than receiving a false all-clear.
        var ct = TestContext.Current.CancellationToken;
        var inner = new NoInventoryStore();
        await inner.SaveAsync("OrphanedProcessor", 42, ct);

        await using var caching = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromHours(1),
            resyncInterval: TimeSpan.FromHours(1));

        // The decorator must honour the inner store's opt-out via CanEnumerate.
        ((ICheckpointInventory)caching).CanEnumerate.Should().BeFalse(
            "a decorator wrapping a non-inventory store must report CanEnumerate = false");

        // The resolution pattern must yield null when CanEnumerate is false.
        ICheckpointInventory? resolved =
            caching is ICheckpointInventory { CanEnumerate: true } inv ? inv : null;
        resolved.Should().BeNull(
            "the resolution pattern must return null for a store that cannot enumerate, " +
            "not an ICheckpointInventory that would return an empty list and look like an all-clear");

        // Passing null to the hosted service under Strict must skip the check, not pass it.
        // "Skip" is the correct outcome: "we don't know" is better than a false all-clear.
        var definition = Definition(OrphanCheckpointPolicy.Strict, "ActiveProcessor");
        var act = () => RunAsync(definition, inventory: null);

        await act.Should().NotThrowAsync(
            "when the store cannot enumerate, the orphan check is skipped — not passed");
    }

    // -----------------------------------------------------------------------
    // Regression: third-party decorator must be able to express CanEnumerate = false
    // -----------------------------------------------------------------------
    // Before the CanEnumerate fix the resolution site named the concrete CachingCheckpointStore
    // type and fell through to a plain `as ICheckpointInventory` cast for every other store.
    // A third-party decorator implementing ICheckpointStore + ICheckpointInventory over a
    // non-enumerable inner had no way to express "I cannot enumerate" through the public
    // interface — the cast always succeeded and returned a live inventory whose ListProcessorIdsAsync
    // returned [], which is indistinguishable from a genuine all-clear under Strict policy.

    /// <summary>
    /// A minimal decorator that uses only the public Alberto API (no internals). Overrides
    /// <see cref="ICheckpointInventory.CanEnumerate"/> to return <c>false</c> when the inner
    /// store does not support enumeration — the pattern the fix enables.
    /// </summary>
    private sealed class ThirdPartyDecorator(ICheckpointStore inner) : ICheckpointStore, ICheckpointInventory
    {
        public int ListProcessorIdsAsyncCallCount { get; private set; }

        // CanEnumerate correctly delegates to the inner store's capability.
        // This override is only expressible after ICheckpointInventory.CanEnumerate was added.
        public bool CanEnumerate => inner is ICheckpointInventory;

        public Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
        {
            ListProcessorIdsAsyncCallCount++;
            return inner is ICheckpointInventory inv
                ? inv.ListProcessorIdsAsync(ct)
                : Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => inner.GetAsync(processorId, ct);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
            => inner.SaveAsync(processorId, position, ct);

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => inner.ResetAsync(processorId, ct);

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => inner.RewindAsync(processorId, position, ct);
    }

    [Fact]
    public async Task ThirdPartyDecorator_CanEnumerateFalse_IsOptedOutByResolutionSite()
    {
        // Regression test for the "shallow seam" defect. The resolution site in
        // ServiceCollectionExtensions.cs used to cast to the concrete CachingCheckpointStore
        // type and fall through to `as ICheckpointInventory` for every other store. A
        // third-party decorator implementing ICheckpointStore + ICheckpointInventory over a
        // non-enumerable inner had no way to express "I cannot enumerate" through the public
        // interface — the cast always succeeded; its ListProcessorIdsAsync returned []; and
        // OrphanCheckpointHostedService saw no orphans even when real ones existed — a false
        // all-clear under Strict policy.
        //
        // After the fix, ICheckpointInventory carries `bool CanEnumerate => true;` (default).
        // The production expression in ServiceCollectionExtensions.cs is now:
        //     store is ICheckpointInventory { CanEnumerate: true } inv ? inv : null
        // A decorator that overrides CanEnumerate to false receives null, and the hosted
        // service logs the skip instead of calling ListProcessorIdsAsync.
        //
        // RED on origin/main: ICheckpointInventory has no CanEnumerate, so ThirdPartyDecorator
        // (which overrides it) does not compile. That is the defect — the interface offered no
        // way to ask the capability question at all.
        //
        // This test goes through the PRODUCTION resolution site (ServiceCollectionExtensions.cs)
        // via real DI, not a local copy of the pattern. If someone reverts that expression to
        // the old `as ICheckpointInventory` cast, the decorator is resolved as a live inventory,
        // ListProcessorIdsAsync is called, and ListProcessorIdsAsyncCallCount becomes 1.
        var ct = TestContext.Current.CancellationToken;

        // Write a real orphan: an empty-list return from ThirdPartyDecorator would be a
        // false all-clear (no orphans detected), which is distinct from a correct skip.
        var inner = new NoInventoryStore();
        await inner.SaveAsync("OrphanedProcessor", 42, ct);
        var decorator = new ThirdPartyDecorator(inner);

        // Build the DI container as a real host would, then replace the keyed ICheckpointStore
        // with the decorator. The last AddKeyedSingleton registration wins for GetKeyedService
        // (singular), so the production lambda receives the decorator when it calls
        // GetKeyedService<ICheckpointStore>("orders").
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddAlberto("orders", m => m.WithInMemory());
        services.AddKeyedSingleton<ICheckpointStore>("orders", decorator);

        await using var sp = services.BuildServiceProvider();

        // Pin the override assumption: if last-registration-wins ever stops applying here,
        // the rest of the test would pass vacuously (InMemoryCheckpointStore.CanEnumerate
        // is true, ListProcessorIdsAsync runs on the inner store, and the count stays 0).
        sp.GetKeyedService<ICheckpointStore>("orders").Should().BeSameAs(decorator,
            "the last AddKeyedSingleton registration must win for GetKeyedService (singular)");

        // OrphanCheckpointHostedService is registered as IHostedService by AddAlberto.
        // Starting it exercises the production resolution expression.
        var orphanService = sp.GetServices<IHostedService>()
            .OfType<OrphanCheckpointHostedService>()
            .Single();
        await orphanService.StartAsync(ct);

        // The production expression must have returned null (CanEnumerate: false → opted-out).
        // If the expression were reverted to the old `as ICheckpointInventory` cast, the
        // decorator would be non-null, ListProcessorIdsAsync would be called, and this fails.
        decorator.ListProcessorIdsAsyncCallCount.Should().Be(0,
            "the production resolution site must treat CanEnumerate: false as opted-out and " +
            "never call ListProcessorIdsAsync on a store that cannot enumerate");
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
            liveLoops: NoLiveLoops.Instance,
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
