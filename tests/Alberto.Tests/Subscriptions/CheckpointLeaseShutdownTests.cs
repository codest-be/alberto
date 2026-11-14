using System.Collections.Concurrent;
using System.Reflection;
using Alberto.InMemory;
using Alberto.Checkpoints;
using Alberto.Leases;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Shutdown flush, per-processor fence-violation handling and rewind/reset vs flush races
/// across <see cref="CachingCheckpointStore"/> and <see cref="LeaseAwareControlLoopGroup"/>.
/// </summary>
public sealed class CheckpointLeaseShutdownTests
{
    private sealed class Lease(long token) : IProcessorLeaseManager
    {
        public TimeSpan LeaseDuration => TimeSpan.FromHours(1);

        public Task<IProcessorLease?> TryAcquireAsync(
            string consumerId, string processorId, string replicaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IProcessorLease?>(
                new ProcessorLease(processorId, DateTimeOffset.UtcNow.AddHours(1), token));

        public Task<IReadOnlyList<string>> RenewLeasesAsync(
            string consumerId, string replicaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task ReleaseAllLeasesAsync(
            string consumerId, string replicaId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

    /// <summary>Fenced store that accepts a write only while the presented token is non-zero.</summary>
    private sealed class TokenFencedStore : IFencedCheckpointStore
    {
        public ConcurrentDictionary<string, long?> Data { get; } = new();

        public Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Data.GetValueOrDefault(processorId));

        public Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
        {
            Data.TryRemove(processorId, out _);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default) =>
            SaveAsync(processorId, position, cancellationToken);

        public Task<bool> SaveIfLeaseHeldAsync(
            string processorId, long position, string consumerId, string replicaId, long fenceToken,
            bool useProcessorLeaseFencing = false, CancellationToken cancellationToken = default)
        {
            if (fenceToken == 0) return Task.FromResult(false);
            Data[processorId] = position;
            return Task.FromResult(true);
        }
    }

    private sealed class StubProcessor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>();
        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ControlLoop BuildLoop(string processorId)
    {
        var backend = new InMemoryEventStoreBackend();
        return new ControlLoop(new StubProcessor(processorId),
            new EventStoreHead(backend, TimeSpan.FromMilliseconds(50)),
            backend, new InMemoryCheckpointStore(),
            pollingInterval: TimeSpan.FromMilliseconds(50), batchSize: 1,
            executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled },
            drainTimeout: TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task StopAsync_FlushesFencedCheckpointsBeforeTokensAndLeasesAreDropped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new TokenFencedStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        LeaseAwareControlLoopGroup? group = null;
        cache.SetFencingContext(new FencingContext("c", "r", true, id => group?.GetFenceToken(id) ?? 0));

        group = new LeaseAwareControlLoopGroup(
            [BuildLoop("p1")], new Lease(7), "c", "r", flushCheckpoints: cache.FlushAsync);
        await group.StartAsync(cancellationToken);
        await cache.SaveAsync("p1", 42, cancellationToken);

        await group.StopAsync(cancellationToken);

        inner.Data["p1"].Should().Be(42, "the final flush has to run while the fence token is still held");
        await group.DisposeAsync();
    }

    [Fact]
    public async Task StopProcessorAsync_StopsOnlyThatProcessorAndLetsScanReacquireIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var group = new LeaseAwareControlLoopGroup(
            [BuildLoop("p1"), BuildLoop("p2")], new Lease(7), "c", "r");
        try
        {
            await group.StartAsync(cancellationToken);

            await group.StopProcessorAsync("p1");

            group.GetFenceToken("p1").Should().Be(0);
            group.GetFenceToken("p2").Should().Be(7, "siblings keep running");

            typeof(LeaseAwareControlLoopGroup)
                .GetMethod("OnScanTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(group, [null]);
            await Task.Delay(200, cancellationToken);

            group.GetFenceToken("p1").Should().Be(7, "a later scan re-acquires the processor");
        }
        finally { await group.DisposeAsync(); }
    }

    /// <summary>Inner store whose first fenced save blocks until released.</summary>
    private sealed class GatedStore : IFencedCheckpointStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? Value { get; private set; }
        private int _first = 1;

        public Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default) => Task.FromResult(Value);

        public Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Value = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
        {
            Value = null;
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default)
        {
            Value = position;
            return Task.CompletedTask;
        }

        public async Task<bool> SaveIfLeaseHeldAsync(
            string processorId, long position, string consumerId, string replicaId, long fenceToken,
            bool useProcessorLeaseFencing = false, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                Entered.SetResult();
                await Gate.Task;
            }

            Value = position;
            return true;
        }
    }

    [Fact]
    public async Task RewindAsync_WaitsForInFlightFlush_SoRewoundPositionWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new GatedStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext("c", "r", true, _ => 1));
        await cache.SaveAsync("p", 100, cancellationToken);

        var flush = cache.FlushAsync(cancellationToken);
        await inner.Entered.Task;
        var rewind = cache.RewindAsync("p", 10, cancellationToken);
        inner.Gate.SetResult();
        await Task.WhenAll(flush, rewind);

        inner.Value.Should().Be(10, "the stale buffered 100 must not land after the rewind");
    }

    [Fact]
    public async Task ResetAsync_WaitsForInFlightFlush_SoResetWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inner = new GatedStore();
        await using var cache = new CachingCheckpointStore(inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        cache.SetFencingContext(new FencingContext("c", "r", true, _ => 1));
        await cache.SaveAsync("p", 100, cancellationToken);

        var flush = cache.FlushAsync(cancellationToken);
        await inner.Entered.Task;
        var reset = cache.ResetAsync("p", cancellationToken);
        inner.Gate.SetResult();
        await Task.WhenAll(flush, reset);

        inner.Value.Should().BeNull("the stale buffered 100 must not land after the reset");
    }
}
