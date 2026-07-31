using System.Diagnostics.Metrics;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for PostgresTenantProcessorLock (tenant-distributed leader election with database-backed leases).
/// </summary>
public class TenantProcessorLockTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TryAcquireForTenant_WhenNoExistingLease_ShouldSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal(tenantId, lease.TenantId);
        Assert.True(lease.ExpiresAt > DateTimeOffset.UtcNow);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_SameTenantSameReplica_ShouldRenewLease()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        // First acquire
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Wall-clock, deliberately: the renewal timestamp is written by PostgreSQL's now(),
        // so the client must wait for real time to pass to verify acquired_at advances.
        await Task.Delay(10, TestContext.Current.CancellationToken);

        // Second acquire same tenant by same replica should renew
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);
        Assert.Equal(tenantId, lease2.TenantId);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_DifferentTenantsSameReplica_ShouldBothSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant-acme-{Guid.NewGuid():N}";
        var tenant2 = $"tenant-contoso-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, replicaId, TestContext.Current.CancellationToken);
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, replicaId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease1);
        Assert.NotNull(lease2);
        Assert.NotEqual(lease1.TenantId, lease2.TenantId);

        // Cleanup
        await lockManager.ReleaseAllLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_SameTenantDifferentReplicas_ShouldOnlyOneSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        // First replica acquires tenant
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second replica should fail for same tenant (lease not expired)
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica2, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_DifferentReplicasDifferentTenants_ShouldBothSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant-acme-{Guid.NewGuid():N}";
        var tenant2 = $"tenant-contoso-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        // First replica claims tenant1
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second replica claims tenant2 - should succeed
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, replica2, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenant1, TestContext.Current.CancellationToken);
        await lockManager.ReleaseLeaseAsync(consumerId, tenant2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_AfterLeaseReleased_ShouldSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        // First replica acquires and releases
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);

        // Second replica should now be able to acquire
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica2, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_AfterLeaseExpired_OtherReplicaCanClaim()
    {
        // Use a very short lease duration for this test
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null, TimeSpan.FromMilliseconds(100));
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        // First replica acquires
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica1, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second replica should fail (lease not expired yet)
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica2, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Wall-clock, deliberately: the lease expiry is evaluated by PostgreSQL's now(),
        // which no client-side TimeProvider can move. Advancing a fake clock here would
        // read as determinism that is not there.
        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Now second replica should succeed
        var lease3 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replica2, TestContext.Current.CancellationToken);
        Assert.NotNull(lease3);
        Assert.Equal(tenantId, lease3.TenantId);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RenewLeases_ShouldExtendExpiry()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null, TimeSpan.FromMilliseconds(200));
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        // Acquire lease
        var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var originalExpiry = lease.ExpiresAt;

        // Wall-clock, deliberately: the renewed expires_at is written by PostgreSQL's now(),
        // so the client must wait for real time to pass before renewing — otherwise the
        // new expires_at would equal the old one and the assertion would be vacuous.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Renew
        var renewedTenants = await lockManager.RenewLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
        Assert.Contains(tenantId, renewedTenants);

        // Acquire again to get new expiry
        var renewedLease = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken);
        Assert.NotNull(renewedLease);
        Assert.True(renewedLease.ExpiresAt > originalExpiry);

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RenewLeases_ShouldOnlyRenewOwnLeases()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant1-{Guid.NewGuid():N}";
        var tenant2 = $"tenant2-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";

        // Replica1 acquires tenant1
        await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, replica1, TestContext.Current.CancellationToken);

        // Replica2 acquires tenant2
        await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, replica2, TestContext.Current.CancellationToken);

        // Renew only replica1's leases
        var renewed = await lockManager.RenewLeasesAsync(consumerId, replica1, TestContext.Current.CancellationToken);

        Assert.Single(renewed);
        Assert.Contains(tenant1, renewed);
        Assert.DoesNotContain(tenant2, renewed);

        // Cleanup
        await lockManager.ReleaseAllLeasesAsync(consumerId, replica1, TestContext.Current.CancellationToken);
        await lockManager.ReleaseAllLeasesAsync(consumerId, replica2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReleaseAllLeases_ShouldReleaseOnlyOwnLeases()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant1-{Guid.NewGuid():N}";
        var tenant2 = $"tenant2-{Guid.NewGuid():N}";
        var replica1 = $"replica1-{Guid.NewGuid():N}";
        var replica2 = $"replica2-{Guid.NewGuid():N}";
        var replica3 = $"replica3-{Guid.NewGuid():N}";

        // Replica1 acquires tenant1
        await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, replica1, TestContext.Current.CancellationToken);

        // Replica2 acquires tenant2
        await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, replica2, TestContext.Current.CancellationToken);

        // Release only replica1's leases
        await lockManager.ReleaseAllLeasesAsync(consumerId, replica1, TestContext.Current.CancellationToken);

        // Replica3 should be able to claim tenant1 (released)
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, replica3, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Replica3 should NOT be able to claim tenant2 (still held by replica2)
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, replica3, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Cleanup
        await lockManager.ReleaseAllLeasesAsync(consumerId, replica2, TestContext.Current.CancellationToken);
        await lockManager.ReleaseAllLeasesAsync(consumerId, replica3, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_MultipleTenants_ShouldTrackAllLeases()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";
        var tenants = Enumerable.Range(1, 5).Select(i => $"tenant-{i}-{Guid.NewGuid():N}").ToList();

        var leases = new List<ITenantLease>();

        // Acquire leases for all tenants
        foreach (var tenant in tenants)
        {
            var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenant, replicaId, TestContext.Current.CancellationToken);
            Assert.NotNull(lease);
            leases.Add(lease);
        }

        // All leases should be acquired
        Assert.Equal(5, leases.Count);

        // Renew should return all 5 tenants
        var renewed = await lockManager.RenewLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
        Assert.Equal(5, renewed.Count);

        // Cleanup
        await lockManager.ReleaseAllLeasesAsync(consumerId, replicaId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LeaseDuration_ShouldBeConfigurable()
    {
        var shortDuration = TimeSpan.FromSeconds(30);
        var longDuration = TimeSpan.FromSeconds(120);

        var shortLock = new PostgresTenantProcessorLock(fixture.DataSource, null, shortDuration);
        var longLock = new PostgresTenantProcessorLock(fixture.DataSource, null, longDuration);

        Assert.Equal(shortDuration, shortLock.LeaseDuration);
        Assert.Equal(longDuration, longLock.LeaseDuration);
    }

    [Fact]
    public async Task LeaseDuration_Default_ShouldBe60Seconds()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);

        Assert.Equal(TimeSpan.FromSeconds(60), lockManager.LeaseDuration);
    }

    // ── Metric tag contract ──────────────────────────────────────────────────
    //
    // MetricTagContractTests pins the tag key set of every instrument that can be
    // driven in-process. These two counters cannot: they are only ever emitted from
    // PostgresTenantProcessorLock, so the assertion has to live where the lock runs.
    //
    // The contract is {consumer.id} and nothing more. A tenant id is unbounded and
    // grows with the customer base; every distinct tag combination is a separate
    // time series the SDK holds for the life of the process and exports on every
    // collection cycle, so tagging by tenant would make these counters most
    // expensive exactly when the product is doing best. tenant.id was carried here
    // until v1 and was removed; these tests are what stop it coming back.

    [Fact]
    public async Task TryAcquireForTenant_OnSuccess_TenantLocksAcquired_isTaggedByConsumerIdOnly()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var replicaId = $"replica-{Guid.NewGuid():N}";

        var tags = await CaptureCounterTagsAsync(
            "alberto.tenant_locks_acquired",
            consumerId,
            () => lockManager.TryAcquireForTenantAsync(consumerId, tenantId, replicaId, TestContext.Current.CancellationToken));

        Assert.Equal(["consumer.id"], TagKeys(tags));

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireForTenant_OnContention_TenantLockFailures_isTaggedByConsumerIdOnly()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource, null);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // Replica A takes the lease, so replica B's attempt is the failure we want to observe.
        var held = await lockManager.TryAcquireForTenantAsync(
            consumerId, tenantId, $"replica-a-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var tags = await CaptureCounterTagsAsync(
            "alberto.tenant_lock_failures",
            consumerId,
            () => lockManager.TryAcquireForTenantAsync(
                consumerId, tenantId, $"replica-b-{Guid.NewGuid():N}", TestContext.Current.CancellationToken));

        Assert.Equal(["consumer.id"], TagKeys(tags));

        // Cleanup
        await lockManager.ReleaseLeaseAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="act"/> with a listener on <paramref name="instrumentName"/> and returns
    /// the tags of the single measurement carrying <paramref name="consumerId"/>. Filtering by the
    /// caller's unique consumer id keeps the assertion stable when other tests emit concurrently —
    /// and still matches if an extra tag is reintroduced, which is the failure this must catch.
    /// </summary>
    private static async Task<KeyValuePair<string, object?>[]> CaptureCounterTagsAsync(
        string instrumentName,
        string consumerId,
        Func<Task<ITenantLease?>> act)
    {
        var captured = new List<KeyValuePair<string, object?>[]>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlbertoMetrics.Name && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            lock (captured) captured.Add(tags.ToArray());
        });
        listener.Start();

        await act();

        listener.Dispose();

        lock (captured)
        {
            return Assert.Single(
                captured,
                t => t.Any(kv => kv.Key == "consumer.id" && (string?)kv.Value == consumerId));
        }
    }

    private static string[] TagKeys(KeyValuePair<string, object?>[] tags) =>
        [.. tags.Select(t => t.Key).Order()];
}
