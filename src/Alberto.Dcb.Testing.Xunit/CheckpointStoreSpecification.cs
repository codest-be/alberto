using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="ICheckpointStore"/> implementations.
///
/// Derive from this class and implement <see cref="CreateStore"/> to run Alberto's own
/// checkpoint-store test suite against your implementation.
/// </summary>
public abstract class CheckpointStoreSpecification
{
    /// <summary>
    /// Unique processor ID generated per test instance for isolation across concurrent runs.
    /// </summary>
    protected string ProcessorId { get; } = $"test-processor-{Guid.NewGuid():N}";

    /// <summary>
    /// Factory method called once per fact to create the store under test.
    /// Return a fresh or shared store; the spec only requires each call to the same fact
    /// to receive the same store for that fact's lifetime.
    /// </summary>
    protected abstract Task<ICheckpointStore> CreateStore();

    /// <summary>
    /// A store with no prior saves must return <see langword="null"/> for any processor ID.
    /// </summary>
    [Fact]
    public async Task Get_WhenNoCheckpoint_ShouldReturnNull()
    {
        var store = await CreateStore();

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    /// <summary>
    /// A position saved with <c>SaveAsync</c> must be returned by a subsequent <c>GetAsync</c>.
    /// </summary>
    [Fact]
    public async Task Save_ThenGet_ShouldReturnSavedPosition()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 42, TestContext.Current.CancellationToken);
        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    /// <summary>
    /// Each subsequent save must replace the previous value for the same processor ID.
    /// </summary>
    [Fact]
    public async Task Save_MultipleTimes_ShouldUpdatePosition()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 10, TestContext.Current.CancellationToken);
        await store.SaveAsync(ProcessorId, 20, TestContext.Current.CancellationToken);
        await store.SaveAsync(ProcessorId, 30, TestContext.Current.CancellationToken);

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(30, result);
    }

    /// <summary>
    /// <c>ResetAsync</c> must remove an existing checkpoint so that <c>GetAsync</c> returns
    /// <see langword="null"/> for that processor ID.
    /// </summary>
    [Fact]
    public async Task Reset_ShouldRemoveCheckpoint()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 100, TestContext.Current.CancellationToken);
        await store.ResetAsync(ProcessorId, TestContext.Current.CancellationToken);

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    /// <summary>
    /// <c>ResetAsync</c> must not throw when no checkpoint exists for the processor ID.
    /// </summary>
    [Fact]
    public async Task Reset_WhenNoCheckpoint_ShouldNotThrow()
    {
        var store = await CreateStore();

        await store.ResetAsync(ProcessorId, TestContext.Current.CancellationToken); // Should not throw

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    /// <summary>
    /// Checkpoints for different processor IDs must be completely isolated from each other.
    /// </summary>
    [Fact]
    public async Task MultipleProcessors_ShouldBeIsolated()
    {
        var store = await CreateStore();
        var processor1 = $"processor-1-{Guid.NewGuid():N}";
        var processor2 = $"processor-2-{Guid.NewGuid():N}";

        await store.SaveAsync(processor1, 100, TestContext.Current.CancellationToken);
        await store.SaveAsync(processor2, 200, TestContext.Current.CancellationToken);

        Assert.Equal(100, await store.GetAsync(processor1, TestContext.Current.CancellationToken));
        Assert.Equal(200, await store.GetAsync(processor2, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <c>RewindAsync</c> must move the checkpoint to the supplied position even when
    /// that position is less than the current checkpoint.
    /// </summary>
    [Fact]
    public async Task Rewind_ShouldMovePositionBackward()
    {
        var store = await CreateStore();

        await store.SaveAsync(ProcessorId, 100, TestContext.Current.CancellationToken);
        await store.RewindAsync(ProcessorId, 50, TestContext.Current.CancellationToken);

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(50, result);
    }

    /// <summary>
    /// <c>RewindAsync</c> must create the checkpoint when none exists yet.
    /// </summary>
    [Fact]
    public async Task Rewind_WhenNoCheckpointExists_ShouldCreateCheckpoint()
    {
        var store = await CreateStore();

        await store.RewindAsync(ProcessorId, 25, TestContext.Current.CancellationToken);

        var result = await store.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(25, result);
    }
}
