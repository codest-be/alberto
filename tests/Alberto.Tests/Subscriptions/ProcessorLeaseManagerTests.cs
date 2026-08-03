using Alberto.Postgres;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for PostgresProcessorLeaseManager (single-leader processing per processor via
/// database-backed leases). This is the lease implementation behind processor fencing —
/// IFencedCheckpointStore and LeaseAwareControlLoopGroup — so its acquire/renew semantics
/// are load-bearing for the guarantee that only one replica advances a processor.
///
/// Uses SingleTenantPostgresFixture deliberately: alberto_processor_leases is created only by
/// the single-tenant migration set (Migrations/SingleTenant/001_InitialSchema.sql:124). The
/// multi-tenant set creates alberto_tenant_leases instead, which PostgresTenantProcessorLock
/// owns. The two lease tables never coexist in one schema.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessorLeaseManagerTests(SingleTenantPostgresFixture fixture)
    : IClassFixture<SingleTenantPostgresFixture>
{
    [Fact]
    public async Task TryAcquire_WhenNoExistingLease_ShouldSucceed()
    {
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await leases.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal(processorId, lease.ProcessorId);

        await leases.ReleaseAllLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquire_WhenHeldByAnotherReplica_ShouldReturnNull()
    {
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        var held = await leases.TryAcquireAsync(
            consumerId, processorId, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var contended = await leases.TryAcquireAsync(
            consumerId, processorId, replica2, TestContext.Current.CancellationToken);
        Assert.Null(contended);

        await leases.ReleaseAllLeasesAsync(consumerId, replica1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquire_BySameReplica_ShouldExtendWithoutResettingAcquiredAt()
    {
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var first = await leases.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        var acquiredAtAfterFirst = (await leases.GetAllLeasesAsync(
            consumerId, TestContext.Current.CancellationToken)).Single().AcquiredAt;

        var second = await leases.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(second);

        var info = (await leases.GetAllLeasesAsync(
            consumerId, TestContext.Current.CancellationToken)).Single();

        // Re-acquiring your own lease extends it but does not restart the holding period.
        Assert.Equal(acquiredAtAfterFirst, info.AcquiredAt);
        Assert.Equal(replicaId, info.ReplicaId);

        await leases.ReleaseAllLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquire_AfterLeaseExpired_OtherReplicaCanClaim()
    {
        var leases = new PostgresProcessorLeaseManager(
            fixture.DataSource, null, TimeSpan.FromMilliseconds(100));
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        var held = await leases.TryAcquireAsync(
            consumerId, processorId, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var tooEarly = await leases.TryAcquireAsync(
            consumerId, processorId, replica2, TestContext.Current.CancellationToken);
        Assert.Null(tooEarly);

        // Wall-clock, deliberately: the lease expiry is evaluated by PostgreSQL's now(),
        // which no client-side TimeProvider can move. Advancing a fake clock here would
        // read as determinism that is not there.
        await Task.Delay(250, TestContext.Current.CancellationToken);

        var stolen = await leases.TryAcquireAsync(
            consumerId, processorId, replica2, TestContext.Current.CancellationToken);
        Assert.NotNull(stolen);
        Assert.Equal(processorId, stolen.ProcessorId);

        await leases.ReleaseAllLeasesAsync(consumerId, replica2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RenewLeases_ShouldExtendExpiryForOwnLeasesOnly()
    {
        var leases = new PostgresProcessorLeaseManager(
            fixture.DataSource, null, TimeSpan.FromSeconds(30));
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var mine = $"processor-mine-{Guid.NewGuid():N}";
        var theirs = $"processor-theirs-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        Assert.NotNull(await leases.TryAcquireAsync(
            consumerId, mine, replica1, TestContext.Current.CancellationToken));
        Assert.NotNull(await leases.TryAcquireAsync(
            consumerId, theirs, replica2, TestContext.Current.CancellationToken));

        var expiryBefore = await ReadExpiryAsync(consumerId, mine);

        // Wall-clock, deliberately: the renewal is stamped by PostgreSQL's now(), so the
        // client must actually wait for real time to pass before renewing — otherwise the
        // new expires_at would equal the old one and the assertion would be vacuous.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var renewed = await leases.RenewLeasesAsync(
            consumerId, replica1, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { mine }, renewed);
        Assert.True(await ReadExpiryAsync(consumerId, mine) > expiryBefore);

        await leases.ReleaseAllLeasesAsync(consumerId, replica1, TestContext.Current.CancellationToken);
        await leases.ReleaseAllLeasesAsync(consumerId, replica2, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Pins the invariant that expires_at is derived from the database clock rather than the
    /// caller's: the stored expiry must sit LeaseDuration away from the database's own now(),
    /// measured entirely inside the database so no client clock enters the comparison.
    ///
    /// Honest limitation: on a host whose clock agrees with the container's, a client-computed
    /// expiry would satisfy this too, so this is a guard rather than a reproducer. Its value is
    /// that it fails loudly on a host that *does* drift — the condition under which mixing the
    /// two clocks silently shortens or extends the lease — instead of surfacing as a flaky
    /// timing test elsewhere in the suite.
    /// </summary>
    [Fact]
    public async Task TryAcquire_ExpiryIsMeasuredAgainstTheDatabaseClock()
    {
        var leaseDuration = TimeSpan.FromSeconds(30);
        var leases = new PostgresProcessorLeaseManager(fixture.DataSource, null, leaseDuration);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var processorId = $"processor-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        Assert.NotNull(await leases.TryAcquireAsync(
            consumerId, processorId, replicaId, TestContext.Current.CancellationToken));

        var remaining = await ReadRemainingLeaseAsync(consumerId, processorId);

        // The lease was just taken, so its remaining life is the full duration minus the round
        // trip. A skewed client clock would push this outside the window in either direction.
        Assert.InRange(remaining, leaseDuration - TimeSpan.FromSeconds(5), leaseDuration);

        await leases.ReleaseAllLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
    }

    private async Task<DateTime> ReadExpiryAsync(string consumerId, string processorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT expires_at
            FROM alberto_processor_leases
            WHERE consumer_id = @consumer_id AND processor_id = @processor_id
            """,
            connection);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        return (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Returns expires_at − now() evaluated inside the database, so the result is independent
    /// of this process's clock.
    /// </summary>
    private async Task<TimeSpan> ReadRemainingLeaseAsync(string consumerId, string processorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT expires_at - now()
            FROM alberto_processor_leases
            WHERE consumer_id = @consumer_id AND processor_id = @processor_id
            """,
            connection);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        return (TimeSpan)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
