using Alberto.InMemory;
using Alberto.Projections;
using Alberto.Subscriptions;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// The rebuild state is read once per interval however many parties want it fresh, and the
/// coordinator's sweep only revisits versions that can still hold state.
/// </summary>
public sealed class RebuildPollingAndSweepTests
{
    private const string ProcessorId = "orders";

    [Fact]
    public async Task ProjectionVersions_DoesNotPollWhileSomeoneElseKeepsItFresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeRebuildStore();
        await using var versions = new ProjectionVersions(store, refreshInterval: TimeSpan.FromMilliseconds(300));

        var manual = 0;
        var until = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < until)
        {
            await versions.RefreshAsync(ct);
            manual++;
            await Task.Delay(20, ct);
        }

        (store.Lists - manual).Should().BeLessThanOrEqualTo(1,
            "the loop's own first read is the only one it makes while the coordinator refreshes every tick");
    }

    [Fact]
    public async Task ProjectionVersions_StillPollsOnItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeRebuildStore();
        await using var versions = new ProjectionVersions(store, refreshInterval: TimeSpan.FromMilliseconds(30));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Lists < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        store.Lists.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Sweep_ReclaimsAfterTheGracePeriod_AndDoesNotRevisitClearedVersions()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var grace = TimeSpan.FromMinutes(1);

        // Promoted to version 3 just now: 1 and 2 are dead but inside the grace period.
        var store = new FakeRebuildStore { State = Settled(RebuildStatus.Completed, active: 3, allocated: 3, time.GetUtcNow()) };
        await using var versions = new ProjectionVersions(store, refreshInterval: TimeSpan.FromSeconds(30));
        using var coordinator = Coordinator(store, versions, time, grace);

        await coordinator.StartAsync(ct);
        await Task.Delay(200, ct);
        store.Discarded.Should().BeEmpty("the grace period has not elapsed");

        time.Advance(grace);
        await WaitForAsync(() => store.Discarded.Count >= 2, ct);
        store.Discarded.Should().Equal(1, 2);

        // An aborted rebuild burned version 4. Only it can hold state now; 1 and 2 were cleared
        // and numbers are never reused, so they are not swept again.
        store.State = Settled(RebuildStatus.Aborted, active: 3, allocated: 4, time.GetUtcNow());
        await Task.Delay(200, ct);
        store.Discarded.Should().Equal([1, 2], "the abort is still inside its grace period");

        time.Advance(grace);
        await WaitForAsync(() => store.Discarded.Count >= 3, ct);
        await Task.Delay(200, ct);

        await coordinator.StopAsync(ct);
        store.Discarded.Should().Equal(1, 2, 4);
    }

    private static ProjectionRebuildState Settled(RebuildStatus status, int active, int allocated, DateTimeOffset completedAt) =>
        new(ProcessorId, ProcessorId, active, RebuildingVersion: null, status,
            StartedAt: completedAt.AddMinutes(-1), TargetPosition: null, CompletedAt: completedAt,
            LastAllocatedVersion: allocated);

    private static RebuildCoordinator Coordinator(
        FakeRebuildStore store, ProjectionVersions versions, TimeProvider time, TimeSpan grace) =>
        new(
            projections: [new RebuildableProjection(ProcessorId, ProcessorId, _ => throw new InvalidOperationException())],
            rebuildStore: store,
            versions: versions,
            checkpoints: new InMemoryCheckpointStore(),
            loopFactory: new ShadowControlLoopFactory(_ => throw new InvalidOperationException()),
            liveLoops: NoLiveLoops.Instance,
            clearers: [],
            options: new RebuildCoordinatorOptions(
                PollingInterval: TimeSpan.FromMilliseconds(20), AutoPromote: false, ReclaimGracePeriod: grace),
            timeProvider: time);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);
    }

    private sealed class FakeRebuildStore : IProjectionRebuildStore, IProjectionRebuildCoordinatorStore
    {
        private int _lists;

        public ProjectionRebuildState? State { get; set; }

        public int Lists => Volatile.Read(ref _lists);

        public List<int> Discarded { get; } = [];

        public Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _lists);
            return Task.FromResult<IReadOnlyList<ProjectionRebuildState>>(State is { } s ? [s] : []);
        }

        public Task DiscardStateVersionAsync(string projectionType, int version, CancellationToken cancellationToken = default)
        {
            lock (Discarded) Discarded.Add(version);
            return Task.CompletedTask;
        }

        public Task<ProjectionRebuildState> GetAsync(string processorId, string projectionType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> StartAsync(string processorId, string projectionType, long targetPosition, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> RequestPromotionAsync(string processorId, bool force = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> RequestAbortAsync(string processorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProjectionRebuildState> MarkReadyAsync(string processorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RebuildOutcome> CompletePromotionAsync(string processorId, bool force = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RebuildOutcome> CompleteAbortAsync(string processorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
