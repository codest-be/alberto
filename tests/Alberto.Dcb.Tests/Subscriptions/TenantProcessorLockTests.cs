using Alberto.Dcb.Postgres;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for PostgresTenantProcessorLock (tenant-distributed leader election with PostgreSQL advisory locks).
/// </summary>
public class TenantProcessorLockTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TryAcquireForTenant_WhenNoExistingLock_ShouldSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);

        // Cleanup
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireForTenant_SameTenantSameConsumer_ShouldReturnExistingLease()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // First acquire
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second acquire same tenant should return existing lease
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // They should be the same lease object
        Assert.Same(lease1, lease2);

        // Cleanup
        await lease1.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireForTenant_DifferentTenantsSameConsumer_ShouldBothSucceed()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant-acme-{Guid.NewGuid():N}";
        var tenant2 = $"tenant-contoso-{Guid.NewGuid():N}";

        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant1, TestContext.Current.CancellationToken);
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenant2, TestContext.Current.CancellationToken);

        Assert.NotNull(lease1);
        Assert.NotNull(lease2);

        // They should be different leases
        Assert.NotSame(lease1, lease2);

        // Cleanup
        await lease1.DisposeAsync();
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireForTenant_SameTenantDifferentInstances_ShouldOnlyOneSucceed()
    {
        var lockManager1 = new PostgresTenantProcessorLock(fixture.DataSource);
        var lockManager2 = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // First instance acquires tenant
        var lease1 = await lockManager1.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second instance (different lock manager) should fail for same tenant
        var lease2 = await lockManager2.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Cleanup
        await lease1.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireForTenant_DifferentInstancesDifferentTenants_ShouldBothSucceed()
    {
        var lockManager1 = new PostgresTenantProcessorLock(fixture.DataSource);
        var lockManager2 = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenant1 = $"tenant-acme-{Guid.NewGuid():N}";
        var tenant2 = $"tenant-contoso-{Guid.NewGuid():N}";

        // First instance claims tenant1
        var lease1 = await lockManager1.TryAcquireForTenantAsync(consumerId, tenant1, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second instance claims tenant2 - should succeed
        var lease2 = await lockManager2.TryAcquireForTenantAsync(consumerId, tenant2, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lease1.DisposeAsync();
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireForTenant_AfterLeaseDisposed_ShouldSucceed()
    {
        var lockManager1 = new PostgresTenantProcessorLock(fixture.DataSource);
        var lockManager2 = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // First instance acquires and releases
        var lease1 = await lockManager1.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);
        await lease1.DisposeAsync();

        // Second instance should now be able to acquire
        var lease2 = await lockManager2.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_MultipleDispose_ShouldNotThrow()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // Multiple dispose should not throw
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task GetLockId_ShouldBeDeterministic()
    {
        var consumerId = "test-consumer";
        var tenantId = "test-tenant";

        var lockId1 = PostgresTenantProcessorLock.GetLockId(consumerId, tenantId);
        var lockId2 = PostgresTenantProcessorLock.GetLockId(consumerId, tenantId);

        Assert.Equal(lockId1, lockId2);
    }

    [Fact]
    public async Task GetLockId_DifferentInputs_ShouldProduceDifferentIds()
    {
        var lockId1 = PostgresTenantProcessorLock.GetLockId("consumer-1", "tenant-a");
        var lockId2 = PostgresTenantProcessorLock.GetLockId("consumer-1", "tenant-b");
        var lockId3 = PostgresTenantProcessorLock.GetLockId("consumer-2", "tenant-a");

        Assert.NotEqual(lockId1, lockId2);
        Assert.NotEqual(lockId1, lockId3);
        Assert.NotEqual(lockId2, lockId3);
    }

    [Fact]
    public async Task TryAcquireForTenant_MultipleTenants_ShouldTrackAllLeases()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenants = Enumerable.Range(1, 5).Select(i => $"tenant-{i}-{Guid.NewGuid():N}").ToList();

        var leases = new List<IAsyncDisposable>();

        // Acquire locks for all tenants
        foreach (var tenant in tenants)
        {
            var lease = await lockManager.TryAcquireForTenantAsync(consumerId, tenant, TestContext.Current.CancellationToken);
            Assert.NotNull(lease);
            leases.Add(lease);
        }

        // Cleanup
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireForTenant_DisposedLeaseAllowsReacquisition()
    {
        var lockManager = new PostgresTenantProcessorLock(fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // Acquire and dispose
        var lease1 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);
        await lease1.DisposeAsync();

        // Same lock manager should be able to reacquire
        var lease2 = await lockManager.TryAcquireForTenantAsync(consumerId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lease2.DisposeAsync();
    }
}
