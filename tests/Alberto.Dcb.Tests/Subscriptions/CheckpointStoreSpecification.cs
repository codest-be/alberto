using Alberto.Dcb.InMemory;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Specification tests for ICheckpointStore implementations.
/// </summary>
public abstract class CheckpointStoreSpecification
{
    /// <summary>
    /// Unique processor ID generated per test instance for isolation.
    /// </summary>
    protected string ProcessorId { get; } = $"test-processor-{Guid.NewGuid():N}";

    /// <summary>
    /// Factory method to create the checkpoint store under test.
    /// </summary>
    protected abstract Task<ICheckpointStore> CreateStore();

    [Fact]
    public async Task Get_WhenNoCheckpoint_ShouldReturnNull()
    {
        var store = await CreateStore();

        var result = await store.GetAsync(ProcessorId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Save_ThenGet_ShouldReturnSavedPosition()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 42);
        var result = await store.GetAsync(ProcessorId);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Save_MultipleTimes_ShouldUpdatePosition()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 10);
        await store.SaveAsync(ProcessorId, 20);
        await store.SaveAsync(ProcessorId, 30);

        var result = await store.GetAsync(ProcessorId);
        Assert.Equal(30, result);
    }

    [Fact]
    public async Task Reset_ShouldRemoveCheckpoint()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 100);
        await store.ResetAsync(ProcessorId);

        var result = await store.GetAsync(ProcessorId);
        Assert.Null(result);
    }

    [Fact]
    public async Task Reset_WhenNoCheckpoint_ShouldNotThrow()
    {
        var store = await CreateStore();

        await store.ResetAsync(ProcessorId); // Should not throw

        var result = await store.GetAsync(ProcessorId);
        Assert.Null(result);
    }

    [Fact]
    public async Task MultipleProcessors_ShouldBeIsolated()
    {
        var store = await CreateStore();
        var processor1 = $"processor-1-{Guid.NewGuid():N}";
        var processor2 = $"processor-2-{Guid.NewGuid():N}";

        await store.SaveAsync(processor1, 100);
        await store.SaveAsync(processor2, 200);

        Assert.Equal(100, await store.GetAsync(processor1));
        Assert.Equal(200, await store.GetAsync(processor2));
    }
}

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
public class PostgresCheckpointStoreTests : CheckpointStoreSpecification, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresCheckpointStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override Task<ICheckpointStore> CreateStore()
    {
        return Task.FromResult<ICheckpointStore>(
            new PostgresCheckpointStore(_fixture.DataSource));
    }
}
