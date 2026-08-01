using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// A private single-tenant database: the tenancy mode whose migration set creates
/// <c>alberto_processor_leases</c>.
/// </summary>
public sealed class FenceTokenSingleTenantFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// A private multi-tenant database, to check that processor fencing is available there too.
/// </summary>
public sealed class FenceTokenMultiTenantFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant);

/// <summary>
/// Covers the fencing guarantee behind <see cref="PostgresCheckpointStore.SaveIfLeaseHeldAsync"/>:
/// a checkpoint write is accepted only from the lease generation that currently owns the
/// processor.
/// </summary>
/// <remarks>
/// Verifying ownership by replica identity alone is not enough. A replica can lose its lease,
/// watch another replica take over and make progress, and then re-acquire the same lease — at
/// which point a write it had already prepared under its <em>earlier</em> ownership matches on
/// replica identity and is accepted. Because the checkpoint upsert is monotonic (<c>GREATEST</c>),
/// such a write can only push the checkpoint forward, so the current owner silently skips every
/// event in between and no automatic path recovers it.
/// <para>
/// The lease therefore carries a generation number that changes on every acquisition. The writer
/// presents the generation it acquired, and the checkpoint row remembers the highest generation
/// that has written to it.
/// </para>
/// </remarks>
public sealed class FencedCheckpointFenceTokenTests(FenceTokenSingleTenantFixture fixture)
    : IClassFixture<FenceTokenSingleTenantFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PostgresCheckpointStore CreateStore() => new(fixture.DataSource);

    private PostgresProcessorLeaseManager CreateLeases() => new(fixture.DataSource);

    /// <summary>
    /// Pushes a lease's expiry into the past so the next acquisition is a genuine takeover.
    /// Faster and more deterministic than waiting out a real lease duration.
    /// </summary>
    private async Task ExpireLeaseAsync(string consumerId, string processorId)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE alberto_processor_leases
            SET expires_at = now() - INTERVAL '1 minute'
            WHERE consumer_id = @consumer_id AND processor_id = @processor_id
            """,
            conn);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        var affected = await cmd.ExecuteNonQueryAsync(Ct);
        affected.Should().Be(1, because: "the lease being expired must exist");
    }

    /// <summary>
    /// The write that the previous implementation accepted: issued by the replica that owns the
    /// lease right now, but carrying the generation from an ownership it has already lost once.
    /// </summary>
    [Fact]
    public async Task WriteFromASupersededLeaseGeneration_IsRejected_EvenWhenTheSameReplicaOwnsTheLeaseAgain()
    {
        var store = CreateStore();
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        // Generation 1: replica A owns the processor and gets as far as position 100.
        var first = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        first.Should().NotBeNull();
        (await store.SaveIfLeaseHeldAsync(
            processorId, 100, consumerId, "replica-a", first!.FenceToken,
            useProcessorLeaseFencing: true, Ct))
            .Should().BeTrue();

        // Generation 2: A stalls, its lease lapses, B takes over and makes progress.
        await ExpireLeaseAsync(consumerId, processorId);
        var second = await leases.TryAcquireAsync(consumerId, processorId, "replica-b", Ct);
        second.Should().NotBeNull();
        (await store.SaveIfLeaseHeldAsync(
            processorId, 200, consumerId, "replica-b", second!.FenceToken,
            useProcessorLeaseFencing: true, Ct))
            .Should().BeTrue();

        // Generation 3: B goes away in turn and A picks the processor back up.
        await ExpireLeaseAsync(consumerId, processorId);
        var third = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        third.Should().NotBeNull();

        // A's flush from generation 1 finally lands. Replica identity matches and the lease is
        // live, so identity-only fencing lets it through — and GREATEST would carry the
        // checkpoint to 500, skipping everything between 200 and 500.
        var stale = await store.SaveIfLeaseHeldAsync(
            processorId, 500, consumerId, "replica-a", first.FenceToken,
            useProcessorLeaseFencing: true, Ct);

        stale.Should().BeFalse(
            because: "generation 1 was superseded the moment replica B acquired the lease");
        (await store.GetAsync(processorId, Ct)).Should().Be(200);
    }

    [Fact]
    public async Task WriteFromTheCurrentLeaseGeneration_IsAccepted()
    {
        var store = CreateStore();
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        lease.Should().NotBeNull();

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId, 42, consumerId, "replica-a", lease!.FenceToken,
            useProcessorLeaseFencing: true, Ct);

        saved.Should().BeTrue();
        (await store.GetAsync(processorId, Ct)).Should().Be(42);
    }

    [Fact]
    public async Task WriteFromAReplicaThatNeverHeldTheLease_IsRejected()
    {
        var store = CreateStore();
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        lease.Should().NotBeNull();

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId, 42, consumerId, "replica-b", lease!.FenceToken,
            useProcessorLeaseFencing: true, Ct);

        saved.Should().BeFalse();
        (await store.GetAsync(processorId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task WriteAfterTheLeaseHasLapsed_IsRejected()
    {
        var store = CreateStore();
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        lease.Should().NotBeNull();
        await ExpireLeaseAsync(consumerId, processorId);

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId, 42, consumerId, "replica-a", lease!.FenceToken,
            useProcessorLeaseFencing: true, Ct);

        saved.Should().BeFalse();
    }

    /// <summary>
    /// The generation is what a running replica presents on every flush, so renewing must not
    /// change it — otherwise every renewal would fence the owner out of its own checkpoint.
    /// </summary>
    [Fact]
    public async Task RenewingALease_LeavesTheGenerationUnchanged()
    {
        var leases = CreateLeases();
        var store = CreateStore();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        lease.Should().NotBeNull();

        var renewed = await leases.RenewLeasesAsync(consumerId, "replica-a", Ct);
        renewed.Should().Contain(processorId);

        (await store.SaveIfLeaseHeldAsync(
            processorId, 7, consumerId, "replica-a", lease!.FenceToken,
            useProcessorLeaseFencing: true, Ct))
            .Should().BeTrue(because: "a renewal is the same ownership, not a new one");
    }

    [Fact]
    public async Task AcquiringAfterAnotherReplicaHeldTheLease_IssuesANewGeneration()
    {
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var first = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        await ExpireLeaseAsync(consumerId, processorId);
        var second = await leases.TryAcquireAsync(consumerId, processorId, "replica-b", Ct);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.FenceToken.Should().BeGreaterThan(first!.FenceToken);
    }

    /// <summary>
    /// Releasing deletes the lease row. If the generation were a per-row counter it would restart
    /// from the beginning here, and a write held over from before the release would match again.
    /// </summary>
    [Fact]
    public async Task AcquiringAfterARelease_DoesNotReuseAnEarlierGeneration()
    {
        var leases = CreateLeases();
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var first = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        await leases.ReleaseAllLeasesAsync(consumerId, "replica-a", Ct);
        var second = await leases.TryAcquireAsync(consumerId, processorId, "replica-b", Ct);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.FenceToken.Should().BeGreaterThan(first!.FenceToken);
    }
}

/// <summary>
/// Processor-lease fencing has to work in both tenancy modes. The control loop turns it on
/// unconditionally (<c>UseProcessorLeaseFencing: true</c>), and the lease manager is registered
/// for every module regardless of tenancy, so a multi-tenant deployment that enables leases runs
/// exactly this path.
/// </summary>
public sealed class FencedCheckpointMultiTenantParityTests(FenceTokenMultiTenantFixture fixture)
    : IClassFixture<FenceTokenMultiTenantFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MultiTenantSchema_SupportsProcessorLeaseFencing()
    {
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource);
        var store = new PostgresCheckpointStore(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(consumerId, processorId, "replica-a", Ct);
        lease.Should().NotBeNull();

        var saved = await store.SaveIfLeaseHeldAsync(
            processorId, 99, consumerId, "replica-a", lease!.FenceToken,
            useProcessorLeaseFencing: true, Ct);

        saved.Should().BeTrue();
        (await store.GetAsync(processorId, Ct)).Should().Be(99);
    }
}
