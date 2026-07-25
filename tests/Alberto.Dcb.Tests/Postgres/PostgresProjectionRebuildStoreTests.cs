using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// A private database on the shared cluster running the shipped single-tenant
/// migrations, including 014 which re-keys the rebuild meta table onto processor_id.
/// No manual schema patching is applied — these tests are also the proof that 014 lands
/// on top of 001 the way it claims to.
/// </summary>
public sealed class PostgresProjectionRebuildStoreFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// Integration tests for <see cref="PostgresProjectionRebuildStore"/>.
///
/// The state machine is the contract the rebuild coordinator and the operator CLI both
/// depend on, so every transition is exercised from both a permitted and a forbidden
/// starting state. The promotion and abort tests additionally assert on the contents of
/// <c>alberto_projection_states</c>, because the version flip is only half the operation —
/// the other half is dropping exactly one version's rows and no others.
/// </summary>
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
        string projectionType, string documentId, int rebuildVersion, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_projection_states
                (projection_type, document_id, rebuild_version, state)
            VALUES (@type, @doc, @version, '{}'::jsonb)
            """, conn);
        cmd.Parameters.AddWithValue("type", projectionType);
        cmd.Parameters.AddWithValue("doc", documentId);
        cmd.Parameters.AddWithValue("version", rebuildVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> CountStateAsync(string projectionType, int rebuildVersion, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM alberto_projection_states
            WHERE projection_type = @type AND rebuild_version = @version
            """, conn);
        cmd.Parameters.AddWithValue("type", projectionType);
        cmd.Parameters.AddWithValue("version", rebuildVersion);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task GetAsync_ProcessorNeverRebuilt_ReportsIdleAtVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();

        var state = await CreateStore().GetAsync(processorId, projectionType, ct);

        state.Status.Should().Be(RebuildStatus.Idle);
        state.ActiveVersion.Should().Be(1);
        state.RebuildingVersion.Should().BeNull();
        state.IsRebuildInFlight.Should().BeFalse();
        state.ProjectionType.Should().Be(projectionType);
    }

    [Fact]
    public async Task StartAsync_AllocatesNextVersion_AndRecordsTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        var started = await store.StartAsync(processorId, projectionType, targetPosition: 4711, ct);

        started.Status.Should().Be(RebuildStatus.Rebuilding);
        started.ActiveVersion.Should().Be(1, "readers must keep seeing the version they were on");
        started.RebuildingVersion.Should().Be(2);
        started.TargetPosition.Should().Be(4711);
        started.StartedAt.Should().NotBeNull();
        started.IsRebuildInFlight.Should().BeTrue();

        // Survives the round trip, so a coordinator restarting mid-rebuild sees the same thing.
        var reread = await store.GetAsync(processorId, projectionType, ct);
        reread.Should().BeEquivalentTo(started);
    }

    [Fact]
    public async Task StartAsync_SecondRebuildWhileOneIsInFlight_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);

        var second = () => store.StartAsync(processorId, projectionType, targetPosition: 20, ct);
        await second.Should().ThrowAsync<RebuildStateException>();

        // The rejected start must not have moved anything.
        var state = await store.GetAsync(processorId, projectionType, ct);
        state.RebuildingVersion.Should().Be(2);
        state.TargetPosition.Should().Be(10);
    }

    [Fact]
    public async Task StartAsync_AfterAReadyRebuild_IsAlsoRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        await store.MarkReadyAsync(processorId, ct);

        var second = () => store.StartAsync(processorId, projectionType, targetPosition: 20, ct);
        await second.Should().ThrowAsync<RebuildStateException>(
            "a rebuilt-but-unpromoted version is still in flight");
    }

    [Fact]
    public async Task MarkReadyAsync_FromRebuilding_Transitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        var ready = await store.MarkReadyAsync(processorId, ct);

        ready.Status.Should().Be(RebuildStatus.Ready);
        ready.RebuildingVersion.Should().Be(2);
        ready.IsRebuildInFlight.Should().BeTrue();
    }

    [Fact]
    public async Task MarkReadyAsync_WithNoRebuildInFlight_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.MarkReadyAsync(processorId, ct);
        await act.Should().ThrowAsync<RebuildStateException>();
    }

    [Fact]
    public async Task PromoteAsync_FromReady_FlipsVersionAndDropsTheSupersededState()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        // Two documents on the live version, one rebuilt document on the shadow version.
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 1, ct);
        await InsertStateAsync(projectionType, "doc-2", rebuildVersion: 1, ct);

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 2, ct);
        await store.MarkReadyAsync(processorId, ct);

        var outcome = await store.PromoteAsync(processorId, force: false, ct);
        var promoted = outcome.State;

        promoted.Status.Should().Be(RebuildStatus.Completed);
        promoted.ActiveVersion.Should().Be(2);
        promoted.RebuildingVersion.Should().BeNull();
        promoted.CompletedAt.Should().NotBeNull();
        promoted.IsRebuildInFlight.Should().BeFalse();
        outcome.DiscardedVersion.Should().Be(
            1, "the coordinator needs the superseded version to clear EF-backed projections, " +
               "which live outside this transaction");

        (await CountStateAsync(projectionType, 1, ct)).Should().Be(
            0, "the superseded version's rows are dropped in the same transaction as the flip");
        (await CountStateAsync(projectionType, 2, ct)).Should().Be(
            1, "the promoted version's rows are exactly what the rebuild wrote");
    }

    [Fact]
    public async Task PromoteAsync_WhileStillRebuilding_IsRejectedUnlessForced()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);

        var unforced = () => store.PromoteAsync(processorId, force: false, ct);
        await unforced.Should().ThrowAsync<RebuildStateException>(
            "publishing a projection that has not finished replaying needs an explicit override");

        // The rejected promote left the rebuild exactly where it was.
        var afterRejection = await store.GetAsync(processorId, projectionType, ct);
        afterRejection.Status.Should().Be(RebuildStatus.Rebuilding);
        afterRejection.ActiveVersion.Should().Be(1);

        var forced = await store.PromoteAsync(processorId, force: true, ct);
        forced.State.ActiveVersion.Should().Be(2);
        forced.State.Status.Should().Be(RebuildStatus.Completed);
        forced.DiscardedVersion.Should().Be(1);
    }

    [Fact]
    public async Task PromoteAsync_WithNoRebuildInFlight_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.PromoteAsync(processorId, force: true, ct);
        await act.Should().ThrowAsync<RebuildStateException>(
            "force overrides an unfinished rebuild, not the absence of one");
    }

    [Fact]
    public async Task AbortAsync_DiscardsThePartialVersion_AndLeavesTheActiveOneAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 1, ct);

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        await InsertStateAsync(projectionType, "doc-1", rebuildVersion: 2, ct);
        await InsertStateAsync(projectionType, "doc-2", rebuildVersion: 2, ct);

        var outcome = await store.AbortAsync(processorId, ct);
        var aborted = outcome.State;

        aborted.Status.Should().Be(RebuildStatus.Aborted);
        aborted.ActiveVersion.Should().Be(1, "readers must not notice an abandoned rebuild");
        aborted.RebuildingVersion.Should().BeNull();
        aborted.IsRebuildInFlight.Should().BeFalse();
        outcome.DiscardedVersion.Should().Be(
            2, "an abort discards the version it abandoned, not the live one");

        (await CountStateAsync(projectionType, 1, ct)).Should().Be(1);
        (await CountStateAsync(projectionType, 2, ct)).Should().Be(
            0, "the partial rebuild's rows are discarded");
    }

    [Fact]
    public async Task AbortAsync_WithNoRebuildInFlight_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, _) = NewIdentity();
        var store = CreateStore();

        var act = () => store.AbortAsync(processorId, ct);
        await act.Should().ThrowAsync<RebuildStateException>();
    }

    [Fact]
    public async Task StartAsync_AfterAPromotion_AllocatesTheVersionAfterTheActiveOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        await store.MarkReadyAsync(processorId, ct);
        await store.PromoteAsync(processorId, force: false, ct);

        var second = await store.StartAsync(processorId, projectionType, targetPosition: 99, ct);

        second.ActiveVersion.Should().Be(2);
        second.RebuildingVersion.Should().Be(
            3, "versions are allocated from the active one, so they never collide with live rows");
        second.CompletedAt.Should().BeNull("the previous rebuild's completion must not linger");
    }

    [Fact]
    public async Task StartAsync_AfterAnAbort_DoesNotReuseTheAbandonedVersionNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);
        await store.AbortAsync(processorId, ct);

        var retried = await store.StartAsync(processorId, projectionType, targetPosition: 20, ct);

        // AbortAsync deleted every row at version 2, but it could not stop the shadow loop that
        // was writing them — that lives in the application process and finds out one poll later.
        // Handing version 2 back would let those late writes seed the retry, and every event
        // would be applied twice.
        retried.ActiveVersion.Should().Be(1, "an abort leaves the version readers see alone");
        retried.RebuildingVersion.Should().Be(3);
        retried.Status.Should().Be(RebuildStatus.Rebuilding);
    }

    [Fact]
    public async Task StartAsync_AllocatesAFreshVersion_ForEveryRebuildHoweverItEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        var seen = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            seen.Add((await store.StartAsync(processorId, projectionType, 10, ct))
                .RebuildingVersion!.Value);
            await store.AbortAsync(processorId, ct);
        }

        var promoted = await store.StartAsync(processorId, projectionType, 10, ct);
        seen.Add(promoted.RebuildingVersion!.Value);
        await store.MarkReadyAsync(processorId, ct);
        await store.PromoteAsync(processorId, force: false, ct);

        seen.Add((await store.StartAsync(processorId, projectionType, 10, ct))
            .RebuildingVersion!.Value);

        seen.Should().Equal([2, 3, 4, 5, 6],
            "the high-water mark advances on every allocation, so no run of aborts can make two "
            + "rebuilds share a version");
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryProcessorWithRecordedState()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        await store.StartAsync(processorId, projectionType, targetPosition: 10, ct);

        var all = await store.ListAsync(ct);

        all.Should().ContainSingle(s => s.ProcessorId == processorId)
            .Which.Status.Should().Be(RebuildStatus.Rebuilding);
    }

    [Fact]
    public async Task ConcurrentStarts_ExactlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var (processorId, projectionType) = NewIdentity();
        var store = CreateStore();

        // The guard is a WHERE on the upsert's DO UPDATE branch, not a read-then-write, so
        // this has to hold without any coordination in the caller.
        var attempts = Enumerable.Range(0, 8)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await store.StartAsync(processorId, projectionType, targetPosition: i, ct);
                    return true;
                }
                catch (RebuildStateException)
                {
                    return false;
                }
            }, ct))
            .ToArray();

        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(won => won).Should().Be(1);
    }
}
