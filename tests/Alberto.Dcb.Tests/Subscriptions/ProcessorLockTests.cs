using Alberto.Dcb.Postgres;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for PostgresProcessorLock (leader election with PostgreSQL advisory locks).
/// </summary>
public class ProcessorLockTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public ProcessorLockTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryAcquire_WhenNoExistingLock_ShouldSucceed()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        var lease = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);

        // Cleanup
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_WhenLockHeld_ShouldReturnNull()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        // First instance acquires
        var lease1 = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second instance should fail
        var lease2 = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Cleanup
        await lease1.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_AfterLeaseDisposed_ShouldSucceed()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        // First acquire and release
        var lease1 = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);
        await lease1.DisposeAsync();

        // Second acquire should succeed
        var lease2 = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);

        // Cleanup
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_DifferentConsumers_ShouldBothSucceed()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumer1 = $"consumer-1-{Guid.NewGuid():N}";
        var consumer2 = $"consumer-2-{Guid.NewGuid():N}";

        var lease1 = await lockManager.TryAcquireAsync(consumer1, TestContext.Current.CancellationToken);
        var lease2 = await lockManager.TryAcquireAsync(consumer2, TestContext.Current.CancellationToken);

        Assert.NotNull(lease1);
        Assert.NotNull(lease2);

        // Cleanup
        await lease1.DisposeAsync();
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_SameConsumerDifferentInstances_ShouldOnlyOneSucceed()
    {
        var lockManager1 = new PostgresProcessorLock(_fixture.DataSource);
        var lockManager2 = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        // First instance acquires
        var lease1 = await lockManager1.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease1);

        // Second instance (different lock manager, same consumer) should fail
        var lease2 = await lockManager2.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.Null(lease2);

        // Cleanup
        await lease1.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ShouldReleaseImmediately()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        // Acquire and immediately release
        var lease = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        await lease.DisposeAsync();

        // Another acquire should work immediately
        var lease2 = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease2);
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_MultipleDispose_ShouldNotThrow()
    {
        var lockManager = new PostgresProcessorLock(_fixture.DataSource);
        var consumerId = $"consumer-{Guid.NewGuid():N}";

        var lease = await lockManager.TryAcquireAsync(consumerId, TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        // Multiple dispose should not throw
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
    }
}
