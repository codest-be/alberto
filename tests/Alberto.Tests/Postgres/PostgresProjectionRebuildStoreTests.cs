using Alberto.Postgres;
using Alberto.Projections;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// A private database on the shared cluster running the shipped single-tenant
/// migrations, including 014 which re-keys the rebuild meta table onto processor_id.
/// No manual schema patching is applied — these tests are also the proof that 014 lands
/// on top of 001 the way it claims to.
/// </summary>
public sealed class PostgresProjectionRebuildStoreFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

internal static class ProjectionRebuildCoordinatorTestExtensions
{
    public static Task<ProjectionRebuildState> MarkReadyAsync(
        this PostgresProjectionRebuildStore store,
        string processorId,
        CancellationToken cancellationToken) =>
        ((IProjectionRebuildCoordinatorStore)store).MarkReadyAsync(processorId, cancellationToken);

    public static Task<RebuildOutcome> PromoteAsync(
        this PostgresProjectionRebuildStore store,
        string processorId,
        bool force,
        CancellationToken cancellationToken) =>
        ((IProjectionRebuildCoordinatorStore)store)
            .CompletePromotionAsync(processorId, force, cancellationToken);

    public static Task<RebuildOutcome> AbortAsync(
        this PostgresProjectionRebuildStore store,
        string processorId,
        CancellationToken cancellationToken) =>
        ((IProjectionRebuildCoordinatorStore)store).CompleteAbortAsync(processorId, cancellationToken);

    public static Task DiscardStateVersionAsync(
        this PostgresProjectionRebuildStore store,
        string projectionType,
        int version,
        CancellationToken cancellationToken) =>
        ((IProjectionRebuildCoordinatorStore)store)
            .DiscardStateVersionAsync(projectionType, version, cancellationToken);
}

/// <summary>
/// Integration tests for <see cref="PostgresProjectionRebuildStore"/>.
///
/// The state machine is the contract the rebuild coordinator and operators both
/// depend on, so every transition is exercised from both a permitted and a forbidden
/// starting state. The promotion and abort tests additionally assert on the contents of
/// <c>alberto_projection_states</c>, because what a transition does to the rows matters as
/// much as what it does to the version numbers — and what it does is deliberately nothing.
/// Reclaiming a discarded version is a separate step, so that a reader holding a version
/// number it resolved a moment before the flip still finds rows behind it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProjectionRebuildStoreTests(PostgresProjectionRebuildStoreFixture fixture)
    : IClassFixture<PostgresProjectionRebuildStoreFixture>
{
    private PostgresProjectionRebuildStore CreateStore() => new(fixture.DataSource);

    /// <summary>Distinct per test so tests sharing the container cannot collide.</summary>
    private static (string ProcessorId, string ProjectionType) NewIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return ($"proc-{suffix}", $"type-{suffix}");
    }

    private async Task InsertStateAsync(
        string projectionType, string documentId, int rebuildVersion, CancellationToken cancellationToken)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_projection_states
                (projection_type, document_id, rebuild_version, state)
            VALUES (@type, @doc, @version, '{}'::jsonb)
            """, conn);
        cmd.Parameters.AddWithValue("type", projectionType);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.AddWithValue("version", rebuildVersion);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountStateAsync(string projectionType, int rebuildVersion, CancellationToken cancellationToken)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM alberto_projection_states
            WHERE projection_type = @type AND rebuild_version = @version
            """, conn);
        cmd.Parameters.AddWithValue("type", projectionType);
        cmd.Parameters.AddWithValue("version", rebuildVersion);
        return (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    [Fact]
    public async Task GetAsync_ProcessorNeverRebuilt_ReportsIdleAtVersionOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();

        var state = await CreateStore().GetAsync(processorId, projectionType, cancellationToken);

        Assert.Equal(RebuildStatus.Idle, state.Status);
        Assert.Equal(1, state.ActiveVersion);
        Assert.Null(state.RebuildingVersion);
        Assert.False(state.IsRebuildInFlight);
        Assert.Equal(projectionType, state.ProjectionType);
    }

    [Fact]
    public async Task StartAsync_AllocatesNextVersion_AndRecordsTarget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        var started = await store.StartAsync(processorId, projectionType, targetPosition: 4711, cancellationToken);

        Assert.Equal(RebuildStatus.Rebuilding, started.Status);
        // readers must keep seeing the version they were on
        Assert.Equal(1, started.ActiveVersion);
        Assert.Equal(2, started.RebuildingVersion);
        Assert.Equal(2, started.LastAllocatedVersion);
        Assert.Equal(4711, started.TargetPosition);
        Assert.NotNull(started.StartedAt);
        Assert.True(started.IsRebuildInFlight);

        // Survives the round trip, so a coordinator restarting mid-rebuild sees the same thing.
        var reread = await store.GetAsync(processorId, projectionType, cancellationToken);
        Assert.Equivalent(started, reread, strict: true);
    }

    [Fact]
    public async Task StartAsync_SecondRebuildWhileOneIsInFlight_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);

        var second = () => store.StartAsync(processorId, projectionType, targetPosition: 20, cancellationToken);
        await Assert.ThrowsAnyAsync<RebuildStateException>(second);

        // The rejected start must not have moved anything.
        var state = await store.GetAsync(processorId, projectionType, cancellationToken);
        Assert.Equal(2, state.RebuildingVersion);
        Assert.Equal(10, state.TargetPosition);
    }

    [Fact]
    public async Task StartAsync_AfterAReadyRebuild_IsAlsoRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await store.MarkReadyAsync(processorId, cancellationToken);

        var second = () => store.StartAsync(processorId, projectionType, targetPosition: 20, cancellationToken);
        // a rebuilt-but-unpromoted version is still in flight
        await Assert.ThrowsAnyAsync<RebuildStateException>(second);
    }

    [Fact]
    public async Task MarkReadyAsync_FromRebuilding_Transitions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        var ready = await store.MarkReadyAsync(processorId, cancellationToken);

        Assert.Equal(RebuildStatus.Ready, ready.Status);
        Assert.Equal(2, ready.RebuildingVersion);
        Assert.True(ready.IsRebuildInFlight);
    }

    [Fact]
    public async Task MarkReadyAsync_WithNoRebuildInFlight_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.MarkReadyAsync(processorId, cancellationToken);
        await Assert.ThrowsAnyAsync<RebuildStateException>(act);
    }

    [Theory]
    [InlineData(false, RebuildOperatorAction.Promote)]
    [InlineData(true, RebuildOperatorAction.ForcePromote)]
    public async Task RequestPromotionAsync_RecordsIntentWithoutFlippingVersions(
        bool force,
        RebuildOperatorAction expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        var requested = await store.RequestPromotionAsync(processorId, force, cancellationToken);

        Assert.Equal(expected, requested.RequestedAction);
        Assert.Equal(RebuildStatus.Rebuilding, requested.Status);
        Assert.Equal(1, requested.ActiveVersion);
        Assert.Equal(2, requested.RebuildingVersion);
    }

    [Fact]
    public async Task RequestAbortAsync_RecordsIntentWithoutDeletingShadowState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await InsertStateAsync(projectionType, "doc", rebuildVersion: 2, cancellationToken);

        var requested = await store.RequestAbortAsync(processorId, cancellationToken);

        Assert.Equal(RebuildOperatorAction.Abort, requested.RequestedAction);
        Assert.Equal(RebuildStatus.Rebuilding, requested.Status);
        Assert.Equal(1, await CountStateAsync(projectionType, 2, cancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_FromReady_FlipsTheVersion_AndLeavesTheSupersededStateForTheSweep()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        // Two documents on the live version, one rebuilt document on the shadow version.
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 1, cancellationToken);
        await InsertStateAsync(projectionType, "doc-2", rebuildVersion: 1, cancellationToken);

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 2, cancellationToken);
        await store.MarkReadyAsync(processorId, cancellationToken);

        var outcome = await store.PromoteAsync(processorId, force: false, cancellationToken);
        var promoted = outcome.State;

        Assert.Equal(RebuildStatus.Completed, promoted.Status);
        Assert.Equal(2, promoted.ActiveVersion);
        Assert.Null(promoted.RebuildingVersion);
        Assert.NotNull(promoted.CompletedAt);
        Assert.False(promoted.IsRebuildInFlight);
        // the coordinator needs the superseded version to reclaim it later
        Assert.Equal(1, outcome.DiscardedVersion);

        // a reader that resolved version 1 just before the flip is still holding that number, and
        // deleting the rows here is what used to leave it seeing nothing
        Assert.Equal(2, await CountStateAsync(projectionType, 1, cancellationToken));
        // the promoted version's rows are exactly what the rebuild wrote
        Assert.Equal(1, await CountStateAsync(projectionType, 2, cancellationToken));

        // The reclaim is a separate step the coordinator takes a grace period later, once no
        // reader can still be holding the superseded version.
        await store.DiscardStateVersionAsync(projectionType, outcome.DiscardedVersion, cancellationToken);

        Assert.Equal(0, await CountStateAsync(projectionType, 1, cancellationToken));
        // reclaiming the superseded version must not touch the promoted one
        Assert.Equal(1, await CountStateAsync(projectionType, 2, cancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_WhileStillRebuilding_IsRejectedUnlessForced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);

        var unforced = () => store.PromoteAsync(processorId, force: false, cancellationToken);
        // publishing a projection that has not finished replaying needs an explicit override
        await Assert.ThrowsAnyAsync<RebuildStateException>(unforced);

        // The rejected promote left the rebuild exactly where it was.
        var afterRejection = await store.GetAsync(processorId, projectionType, cancellationToken);
        Assert.Equal(RebuildStatus.Rebuilding, afterRejection.Status);
        Assert.Equal(1, afterRejection.ActiveVersion);

        var forced = await store.PromoteAsync(processorId, force: true, cancellationToken);
        Assert.Equal(2, forced.State.ActiveVersion);
        Assert.Equal(RebuildStatus.Completed, forced.State.Status);
        Assert.Equal(1, forced.DiscardedVersion);
    }

    [Fact]
    public async Task PromoteAsync_WithNoRebuildInFlight_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.PromoteAsync(processorId, force: true, cancellationToken);
        // force overrides an unfinished rebuild, not the absence of one
        await Assert.ThrowsAnyAsync<RebuildStateException>(act);
    }

    [Fact]
    public async Task AbortAsync_DiscardsThePartialVersion_AndLeavesTheActiveOneAlone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 1, cancellationToken);

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 2, cancellationToken);
        await InsertStateAsync(projectionType, "doc-2", rebuildVersion: 2, cancellationToken);

        var outcome = await store.AbortAsync(processorId, cancellationToken);
        var aborted = outcome.State;

        Assert.Equal(RebuildStatus.Aborted, aborted.Status);
        // readers must not notice an abandoned rebuild
        Assert.Equal(1, aborted.ActiveVersion);
        Assert.Null(aborted.RebuildingVersion);
        Assert.False(aborted.IsRebuildInFlight);
        // an abort discards the version it abandoned, not the live one
        Assert.Equal(2, outcome.DiscardedVersion);

        Assert.Equal(1, await CountStateAsync(projectionType, 1, cancellationToken));
        // the abandoned version is unreachable, but its rows wait for the sweep — the shadow loop that
        // wrote them has not necessarily noticed the abort yet, so a delete here would race its last
        // writes rather than remove them
        Assert.Equal(2, await CountStateAsync(projectionType, 2, cancellationToken));

        await store.DiscardStateVersionAsync(projectionType, outcome.DiscardedVersion, cancellationToken);

        // reclaiming the abandoned version must not touch the live one
        Assert.Equal(1, await CountStateAsync(projectionType, 1, cancellationToken));
        Assert.Equal(0, await CountStateAsync(projectionType, 2, cancellationToken));
    }

    [Fact]
    public async Task DiscardStateVersionAsync_LargerThanOneBatch_RemovesEveryRowOfTheVersionAndNothingElse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, projectionType) = NewIdentity();
        var (_, otherType) = NewIdentity();
        const int rows = PostgresProjectionRebuildStore.DiscardBatchSize * 2 + 3;

        await using (var conn = await fixture.DataSource.OpenConnectionAsync(cancellationToken))
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_projection_states (projection_type, document_id, rebuild_version, state)
            SELECT @type, 'doc-' || g, 2, '{}'::jsonb FROM generate_series(1, @rows) g
            UNION ALL SELECT @type, 'doc-' || g, v, '{}'::jsonb FROM generate_series(1, 3) g, (VALUES (1), (3)) AS v(v)
            UNION ALL SELECT @other, 'doc-' || g, 2, '{}'::jsonb FROM generate_series(1, 3) g
            """, conn))
        {
            cmd.Parameters.AddWithValue("type", projectionType);
            cmd.Parameters.AddWithValue("other", otherType);
            cmd.Parameters.AddWithValue("rows", rows);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await CreateStore().DiscardStateVersionAsync(projectionType, 2, cancellationToken);

        Assert.Equal(0, await CountStateAsync(projectionType, 2, cancellationToken));
        Assert.Equal(3, await CountStateAsync(projectionType, 1, cancellationToken));
        Assert.Equal(3, await CountStateAsync(projectionType, 3, cancellationToken));
        Assert.Equal(3, await CountStateAsync(otherType, 2, cancellationToken));
    }

    [Fact]
    public async Task DiscardStateVersionAsync_ForAVersionHoldingNothing_IsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, projectionType) = NewIdentity();
        var store = CreateStore();

        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 1, cancellationToken);

        // The sweep runs on every replica, and each one's first sweep covers the whole version
        // range, so reclaiming a version that another replica already reclaimed — or that never
        // held anything — has to be ordinary rather than an error.
        await store.DiscardStateVersionAsync(projectionType, 7, cancellationToken);
        await store.DiscardStateVersionAsync(projectionType, 7, cancellationToken);

        Assert.Equal(1, await CountStateAsync(projectionType, 1, cancellationToken));
    }

    [Fact]
    public async Task AbortAsync_WithNoRebuildInFlight_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.AbortAsync(processorId, cancellationToken);
        await Assert.ThrowsAnyAsync<RebuildStateException>(act);
    }

    [Fact]
    public async Task StartAsync_AfterAPromotion_AllocatesTheVersionAfterTheActiveOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await store.MarkReadyAsync(processorId, cancellationToken);
        await store.PromoteAsync(processorId, force: false, cancellationToken);

        var second = await store.StartAsync(processorId, projectionType, targetPosition: 99, cancellationToken);

        Assert.Equal(2, second.ActiveVersion);
        // versions are allocated from the active one, so they never collide with live rows
        Assert.Equal(3, second.RebuildingVersion);
        // the previous rebuild's completion must not linger
        Assert.Null(second.CompletedAt);
    }

    [Fact]
    public async Task StartAsync_AfterAnAbort_DoesNotReuseTheAbandonedVersionNumber()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);
        await store.AbortAsync(processorId, cancellationToken);

        var retried = await store.StartAsync(processorId, projectionType, targetPosition: 20, cancellationToken);

        // AbortAsync deleted every row at version 2, but it could not stop the shadow loop that
        // was writing them — that lives in the application process and finds out one poll later.
        // Handing version 2 back would let those late writes seed the retry, and every event
        // would be applied twice.
        // an abort leaves the version readers see alone
        Assert.Equal(1, retried.ActiveVersion);
        Assert.Equal(3, retried.RebuildingVersion);
        Assert.Equal(RebuildStatus.Rebuilding, retried.Status);
    }

    [Fact]
    public async Task StartAsync_AllocatesAFreshVersion_ForEveryRebuildHoweverItEnded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        var seen = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            seen.Add((await store.StartAsync(processorId, projectionType, 10, cancellationToken))
                .RebuildingVersion!.Value);
            await store.AbortAsync(processorId, cancellationToken);
        }

        var promoted = await store.StartAsync(processorId, projectionType, 10, cancellationToken);
        seen.Add(promoted.RebuildingVersion!.Value);
        await store.MarkReadyAsync(processorId, cancellationToken);
        await store.PromoteAsync(processorId, force: false, cancellationToken);

        seen.Add((await store.StartAsync(processorId, projectionType, 10, cancellationToken))
            .RebuildingVersion!.Value);

        // the high-water mark advances on every allocation, so no run of aborts can make two rebuilds
        // share a version
        Assert.Equal([2, 3, 4, 5, 6], seen);
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryProcessorWithRecordedState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, cancellationToken);

        var all = await store.ListAsync(cancellationToken);

        Assert.Equal(RebuildStatus.Rebuilding, Assert.Single(all, s => s.ProcessorId == processorId).Status);
    }

    [Fact]
    public async Task ConcurrentStarts_ExactlyOneWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        // The guard is a WHERE on the upsert's DO UPDATE branch, not a read-then-write, so
        // this has to hold without any coordination in the caller.
        var attempts = Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await store.StartAsync(processorId, projectionType, targetPosition: i, cancellationToken);
                    return true;
                }
                catch (RebuildStateException)
                {
                    return false;
                }
            }, cancellationToken))
            .ToArray();

        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(won => won));
    }
}
