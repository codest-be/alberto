using Alberto.Dcb.InMemory;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for InMemoryCheckpointStore.
/// </summary>
public class InMemoryCheckpointStoreTests : CheckpointStoreSpecification
{
    private readonly InMemoryCheckpointStore _store = new();

    protected override Task<ICheckpointStore> CreateStore()
    {
        return Task.FromResult<ICheckpointStore>(_store);
    }
}

/// <summary>
/// Tests for PostgresCheckpointStore.
/// Uses a shared Testcontainers PostgreSQL instance.
/// </summary>
public class PostgresCheckpointStoreTests(PostgresFixture fixture)
    : CheckpointStoreSpecification, IClassFixture<PostgresFixture>
{
    protected override Task<ICheckpointStore> CreateStore()
    {
        return Task.FromResult<ICheckpointStore>(
            new PostgresCheckpointStore(fixture.DataSource));
    }

    /// <summary>
    /// Postgres uses GREATEST in SaveAsync so a backward save is silently discarded.
    /// This is a Postgres-specific invariant; InMemory does not enforce monotonicity.
    /// </summary>
    [Fact]
    public async Task Save_BackwardPosition_ShouldNotDecrease()
    {
        var store = await CreateStore();
        var processorId = $"test-processor-{Guid.NewGuid():N}";

        await store.SaveAsync(processorId, 100, TestContext.Current.CancellationToken);
        await store.SaveAsync(processorId, 50, TestContext.Current.CancellationToken); // attempt to go back

        var result = await store.GetAsync(processorId, TestContext.Current.CancellationToken);
        Assert.Equal(100, result); // GREATEST preserves the higher value
    }
}
