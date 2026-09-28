using Alberto.InMemory;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Behavioural specification every <see cref="IProcessorFaultStore"/> implementation must meet.
/// Lives here rather than in <c>Alberto.Testing.Xunit</c> because the interface is internal:
/// it is a store capability discovered by <see cref="ControlLoop"/>, not consumer surface.
/// </summary>
public abstract class ProcessorFaultStoreSpecification
{
    /// <summary>Unique per test-class instance so parallel runs never collide on a shared database.</summary>
    protected string ProcessorId { get; } = $"fault-spec-{Guid.NewGuid():N}";

    /// <summary>The store under test. Must implement <see cref="IProcessorFaultStore"/>.</summary>
    protected abstract Task<ICheckpointStore> CreateStore();

    private async Task<(ICheckpointStore Checkpoints, IProcessorFaultStore Faults)> CreateAsync()
    {
        var store = await CreateStore();
        return (store, (IProcessorFaultStore)store);
    }

    private static ProcessorFaultRecord SampleFault(
        long? position = 7,
        string? tenantId = "tenant-1") =>
        new(
            FaultedAt: new DateTimeOffset(2026, 9, 29, 12, 34, 56, TimeSpan.Zero),
            Message: "Simulated fault",
            StackTrace: "at Frame.One()\n   at Frame.Two()",
            Position: position,
            EventType: "test-event-a",
            TenantId: tenantId);

    [Fact]
    public async Task GetFault_WhenNoneRecorded_ReturnsNull()
    {
        var (_, faults) = await CreateAsync();

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordFault_RoundTripsEveryField()
    {
        var (_, faults) = await CreateAsync();
        var fault = SampleFault();

        await faults.RecordFaultAsync(ProcessorId, fault, TestContext.Current.CancellationToken);
        var stored = await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(fault.FaultedAt, stored.FaultedAt);
        Assert.Equal("Simulated fault", stored.Message);
        Assert.Equal("at Frame.One()\n   at Frame.Two()", stored.StackTrace);
        Assert.Equal(7L, stored.Position);
        Assert.Equal("test-event-a", stored.EventType);
        Assert.Equal("tenant-1", stored.TenantId);
    }

    [Fact]
    public async Task RecordFault_WithNullContext_RoundTripsNulls()
    {
        // Batch dispatch and producer-side failures have no single known event.
        var (_, faults) = await CreateAsync();
        var fault = SampleFault(position: null, tenantId: null) with { StackTrace = null, EventType = null };

        await faults.RecordFaultAsync(ProcessorId, fault, TestContext.Current.CancellationToken);
        var stored = await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal("Simulated fault", stored.Message);
        Assert.Null(stored.StackTrace);
        Assert.Null(stored.Position);
        Assert.Null(stored.EventType);
        Assert.Null(stored.TenantId);
    }

    [Fact]
    public async Task RecordFault_Twice_KeepsTheLatest()
    {
        var (_, faults) = await CreateAsync();

        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);
        var newer = SampleFault(position: 9) with
        {
            FaultedAt = new DateTimeOffset(2026, 9, 30, 1, 2, 3, TimeSpan.Zero),
            Message = "Second fault",
        };
        await faults.RecordFaultAsync(ProcessorId, newer, TestContext.Current.CancellationToken);

        var stored = await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("Second fault", stored.Message);
        Assert.Equal(9L, stored.Position);
        Assert.Equal(newer.FaultedAt, stored.FaultedAt);
    }

    [Fact]
    public async Task RecordFault_DoesNotMoveTheCheckpoint()
    {
        var (checkpoints, faults) = await CreateAsync();
        await checkpoints.SaveAsync(ProcessorId, 42, TestContext.Current.CancellationToken);

        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        Assert.Equal(42L, await checkpoints.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordFault_WithNoCheckpoint_LeavesResumePositionAtStart()
    {
        // Postgres inserts a carrier row at position 0; InMemory stores no checkpoint at all.
        // Both must resume from the start: callers coalesce a missing checkpoint to 0.
        var (checkpoints, faults) = await CreateAsync();

        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        var checkpoint = await checkpoints.GetAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(0L, checkpoint ?? 0L);
    }

    [Fact]
    public async Task SaveAsync_DoesNotClearTheFault()
    {
        // Clearing is ControlLoop's explicit decision (once per healthy run), not a side
        // effect of every save.
        var (checkpoints, faults) = await CreateAsync();
        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        await checkpoints.SaveAsync(ProcessorId, 10, TestContext.Current.CancellationToken);

        Assert.NotNull(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearFault_RemovesIt()
    {
        var (_, faults) = await CreateAsync();
        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        await faults.ClearFaultAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearFault_WhenNoneRecorded_IsANoOp()
    {
        var (_, faults) = await CreateAsync();

        await faults.ClearFaultAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetAsync_ClearsTheFault()
    {
        var (checkpoints, faults) = await CreateAsync();
        await checkpoints.SaveAsync(ProcessorId, 42, TestContext.Current.CancellationToken);
        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        await checkpoints.ResetAsync(ProcessorId, TestContext.Current.CancellationToken);

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RewindAsync_ClearsTheFault()
    {
        // A rewind is an operator's retry intent — the stale fault must not outlive it.
        var (checkpoints, faults) = await CreateAsync();
        await checkpoints.SaveAsync(ProcessorId, 42, TestContext.Current.CancellationToken);
        await faults.RecordFaultAsync(ProcessorId, SampleFault(), TestContext.Current.CancellationToken);

        await checkpoints.RewindAsync(ProcessorId, 10, TestContext.Current.CancellationToken);

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
        Assert.Equal(10L, await checkpoints.GetAsync(ProcessorId, TestContext.Current.CancellationToken));
    }
}

public sealed class InMemoryProcessorFaultStoreTests : ProcessorFaultStoreSpecification
{
    private readonly InMemoryCheckpointStore _store = new();

    protected override Task<ICheckpointStore> CreateStore() =>
        Task.FromResult<ICheckpointStore>(_store);

    [Fact]
    public async Task Clear_RemovesTheFault()
    {
        var faults = (IProcessorFaultStore)_store;
        await faults.RecordFaultAsync(ProcessorId,
            new ProcessorFaultRecord(DateTimeOffset.UnixEpoch, "boom", null, null, null, null),
            TestContext.Current.CancellationToken);

        _store.Clear();

        Assert.Null(await faults.GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken));
    }
}

public sealed class CachingProcessorFaultStoreTests : ProcessorFaultStoreSpecification, IAsyncDisposable
{
    private readonly InMemoryCheckpointStore _inner = new();
    private readonly CachingCheckpointStore _cache;

    public CachingProcessorFaultStoreTests()
    {
        _cache = new CachingCheckpointStore(_inner, flushInterval: TimeSpan.FromHours(1));
    }

    protected override Task<ICheckpointStore> CreateStore() =>
        Task.FromResult<ICheckpointStore>(_cache);

    [Fact]
    public async Task RecordFault_BypassesTheWriteBuffer()
    {
        // A buffered fault write would be lost in exactly the crash it exists to explain, so
        // the record must be on the inner store immediately — no flush has run (interval 1h).
        var faults = (IProcessorFaultStore)_cache;
        var fault = new ProcessorFaultRecord(DateTimeOffset.UnixEpoch, "boom", null, 3, "t", null);

        await faults.RecordFaultAsync(ProcessorId, fault, TestContext.Current.CancellationToken);

        var onInner = await ((IProcessorFaultStore)_inner)
            .GetFaultAsync(ProcessorId, TestContext.Current.CancellationToken);
        Assert.Equal(fault, onInner);
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}

/// <summary>
/// The caching store over an inner store without fault support: every fault operation must be
/// an inert no-op rather than a cast failure.
/// </summary>
public sealed class CachingProcessorFaultStoreWithoutSupportTests : IAsyncDisposable
{
    private sealed class PlainCheckpointStore : ICheckpointStore
    {
        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private readonly CachingCheckpointStore _cache =
        new(new PlainCheckpointStore(), flushInterval: TimeSpan.FromHours(1));

    [Fact]
    public async Task FaultOperations_AreNoOps()
    {
        var faults = (IProcessorFaultStore)_cache;
        var fault = new ProcessorFaultRecord(DateTimeOffset.UnixEpoch, "boom", null, null, null, null);

        await faults.RecordFaultAsync("p", fault, TestContext.Current.CancellationToken);
        await faults.ClearFaultAsync("p", TestContext.Current.CancellationToken);

        Assert.Null(await faults.GetFaultAsync("p", TestContext.Current.CancellationToken));
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}

/// <summary>
/// PostgresCheckpointStore against the shared Testcontainers instance.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProcessorFaultStoreTests(PostgresFixture fixture)
    : ProcessorFaultStoreSpecification, IClassFixture<PostgresFixture>
{
    protected override Task<ICheckpointStore> CreateStore() =>
        Task.FromResult<ICheckpointStore>(new PostgresCheckpointStore(fixture.DataSource));
}
