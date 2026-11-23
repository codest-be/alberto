using System.Collections.Concurrent;
using System.Text.Json;
using Alberto.DeadLetters;
using Alberto.InMemory;
using Alberto.Leases;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Alberto.Tests.Subscriptions;

public sealed class DeadLetterRetryLeaseTests
{
    private const string ProcessorId = "retry-lease";
    private const string EventTypeId = "retry-lease-event";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public sealed record Call(string Replica, Guid EventId, bool Retry, DateTime Start, DateTime End);

    private sealed class Count
    {
        private int _value;
        private readonly ConcurrentBag<(int Target, TaskCompletionSource Done)> _waiters = [];

        public int Value => Volatile.Read(ref _value);

        public void Add(int amount = 1)
        {
            var value = Interlocked.Add(ref _value, amount);
            foreach (var waiter in _waiters)
                if (value >= waiter.Target)
                    waiter.Done.TrySetResult();
        }

        public Task Reached(int target)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, done));
            if (Value >= target)
                done.TrySetResult();
            return done.Task.WaitAsync(Patience);
        }
    }

    private static async Task WorkFor(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new CancellationTokenSource(duration);
        using var registration = timer.Token.Register(() => finished.TrySetResult());
        await finished.Task.WaitAsync(cancellationToken);
    }

    private sealed class Processor(string replica, TimeSpan work, ConcurrentDictionary<Guid, bool> retried, Count clusterDone)
        : IEventProcessor
    {
        public string Replica { get; } = replica;
        public ConcurrentQueue<Call> Calls { get; } = new();
        public Count Done { get; } = new();
        public ConcurrentQueue<Guid> RetriesStarted { get; } = new();
        public Func<CancellationToken, Task>? BlockRetries { get; set; }

        public string ProcessorId => DeadLetterRetryLeaseTests.ProcessorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string> { EventTypeId };

        public async Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
        {
            var start = DateTime.UtcNow;
            var retry = retried.ContainsKey(@event.Id);
            if (retry)
            {
                RetriesStarted.Enqueue(@event.Id);
                if (BlockRetries is { } block)
                    await block(cancellationToken);
            }
            await WorkFor(work, cancellationToken);
            Calls.Enqueue(new Call(Replica, @event.Id, retry, start, DateTime.UtcNow));
            Done.Add();
            clusterDone.Add();
        }
    }

    private sealed class CountingStore(IClaimableDeadLetterStore inner) : IClaimableDeadLetterStore
    {
        private readonly ConcurrentDictionary<string, Count> _claimCalls = new();
        private readonly ConcurrentDictionary<string, Count> _claimed = new();

        public Count ClaimCallsBy(string replica) => _claimCalls.GetOrAdd(replica, _ => new Count());
        public Count ClaimedBy(string replica) => _claimed.GetOrAdd(replica, _ => new Count());

        public async Task<IReadOnlyList<DeadLetterClaim>> ClaimDueAsync(
            string processorId, int batchSize, TimeSpan leaseDuration, string claimedBy, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var claims = await inner.ClaimDueAsync(processorId, batchSize, leaseDuration, claimedBy, now, cancellationToken);
            ClaimedBy(claimedBy).Add(claims.Count);
            ClaimCallsBy(claimedBy).Add();
            return claims;
        }

        public async Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId, int batchSize, TimeSpan leaseDuration, string claimedBy,
            CancellationToken cancellationToken = default)
        {
            var claims = await inner.ClaimRetryRequestedAsync(processorId, batchSize, leaseDuration, claimedBy, cancellationToken);
            ClaimedBy(claimedBy).Add(claims.Count);
            ClaimCallsBy(claimedBy).Add();
            return claims;
        }

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default) =>
            inner.StoreAsync(entry, cancellationToken);
        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(string processorId, string? tenantId = null, int limit = 100, CancellationToken cancellationToken = default) =>
            inner.GetAsync(processorId, tenantId, limit, cancellationToken);
        public Task<int> CountAsync(string processorId, CancellationToken cancellationToken = default) =>
            inner.CountAsync(processorId, cancellationToken);
        public Task ClearAsync(string processorId, CancellationToken cancellationToken = default) =>
            inner.ClearAsync(processorId, cancellationToken);
        public Task MarkForRetryAsync(string processorId, CancellationToken cancellationToken = default) =>
            inner.MarkForRetryAsync(processorId, cancellationToken);
        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken cancellationToken = default) =>
            inner.CompleteRetryAsync(claim, cancellationToken);
        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken cancellationToken = default) =>
            inner.AbandonRetryAsync(claim, cancellationToken);
    }

    private sealed record Replica(Processor Processor, LeaseAwareControlLoopGroup Group, DeadLetterRetryLoop Retries)
    {
        public string Name => Processor.Replica;
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<Guid, bool> _retried = new();
        private readonly InMemoryEventStoreBackend _backend = new();
        private readonly InMemoryFencedCheckpointStore _checkpoints;
        private readonly InMemoryProcessorLeaseManager _leases;
        private readonly EventStoreHead _head;

        public Cluster(TimeSpan leaseDuration)
        {
            _leases = new InMemoryProcessorLeaseManager(leaseDuration: leaseDuration);
            _checkpoints = new InMemoryFencedCheckpointStore(_leases);
            _head = new EventStoreHead(_backend, TimeSpan.FromMilliseconds(10));
        }

        public CountingStore DeadLetters { get; } = new(new InMemoryDeadLetterStore());
        public List<Replica> Replicas { get; } = [];
        public Count Done { get; } = new();

        public Replica Add(TimeSpan work, TimeSpan claimLease, int retryBatch = 10)
        {
            var name = $"replica-{Replicas.Count}";
            var processor = new Processor(name, work, _retried, Done);
            var group = new LeaseAwareControlLoopGroup(
                [new ControlLoop(processor, _head, _backend, _checkpoints, pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: 3,
                    executionOptions: new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled })],
                _leases, "c", name);
            var retries = new DeadLetterRetryLoop(
                processor, DeadLetters, TimeSpan.FromMilliseconds(10), retryBatch,
                claimLeaseDuration: claimLease, claimedBy: name, heldLoop: group.HeldLoop);
            var replica = new Replica(processor, group, retries);
            Replicas.Add(replica);
            return replica;
        }

        public async Task StartAsync(Replica replica, CancellationToken cancellationToken)
        {
            await replica.Group.StartAsync(cancellationToken);
            await replica.Retries.StartAsync(cancellationToken);
        }

        public async Task StartAllAsync(CancellationToken cancellationToken)
        {
            await _head.StartAsync(cancellationToken);
            foreach (var replica in Replicas)
                await StartAsync(replica, cancellationToken);
        }

        public async Task<Replica> HolderAfterOneLiveEventAsync(CancellationToken cancellationToken)
        {
            var before = Done.Value;
            await AppendAsync(1, cancellationToken);
            await Done.Reached(before + 1);
            return Replicas.Single(r => !r.Processor.Calls.IsEmpty);
        }

        public async Task<IReadOnlyList<Guid>> DeadLetterAsync(int count, CancellationToken cancellationToken)
        {
            var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
            foreach (var id in ids)
            {
                _retried[id] = true;
                await DeadLetters.StoreAsync(new DeadLetterEntry
                {
                    Id = Guid.NewGuid(),
                    ProcessorId = ProcessorId,
                    EventId = id,
                    EventType = EventTypeId,
                    EventData = "{}",
                    ErrorMessage = "failed",
                    StackTrace = null,
                    AttemptCount = 1,
                    FailedAt = DateTimeOffset.UtcNow,
                    RetryRequested = true,
                }, cancellationToken);
            }

            return ids;
        }

        public Task AppendAsync(int count, CancellationToken cancellationToken) =>
            _backend.AppendAsync(
                [.. Enumerable.Range(0, count).Select(_ => new EventToPersist
                {
                    EventType = new EventType(EventTypeId),
                    Tags = [],
                    EventData = "{}",
                })],
                cancellationToken: cancellationToken);

        public List<Call> Calls => Replicas.SelectMany(r => r.Processor.Calls).OrderBy(c => c.Start).ToList();

        public async ValueTask DisposeAsync()
        {
            foreach (var replica in Replicas)
            {
                await replica.Retries.StopAsync(CancellationToken.None);
                await replica.Retries.DisposeAsync();
                await replica.Group.StopAsync(CancellationToken.None);
                await replica.Group.DisposeAsync();
            }
            await _head.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TwoReplicas_OnlyTheLeaseHolderClaimsAndRetries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var cluster = new Cluster(TimeSpan.FromSeconds(30));
        cluster.Add(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        cluster.Add(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        await cluster.StartAllAsync(token);
        var holder = await cluster.HolderAfterOneLiveEventAsync(token);
        var other = cluster.Replicas.Single(r => r != holder);

        var ids = await cluster.DeadLetterAsync(5, token);
        await cluster.Done.Reached(6);
        var holderClaimCalls = cluster.DeadLetters.ClaimCallsBy(holder.Name);
        await holderClaimCalls.Reached(holderClaimCalls.Value + 20);

        Assert.All(cluster.Calls, c => Assert.Equal(holder.Name, c.Replica));
        Assert.Equal(ids.Order(), cluster.Calls.Where(c => c.Retry).Select(c => c.EventId).Order());
        Assert.Equal(0, cluster.DeadLetters.ClaimCallsBy(other.Name).Value);
        Assert.Equal(5, cluster.DeadLetters.ClaimedBy(holder.Name).Value);
        Assert.Equal(0, await cluster.DeadLetters.CountAsync(ProcessorId, token));
    }

    [Fact]
    public async Task OnTheHolder_ARetryNeverRunsAtTheSameMomentAsALiveEvent()
    {
        var token = TestContext.Current.CancellationToken;
        var work = TimeSpan.FromMilliseconds(15);
        await using var cluster = new Cluster(TimeSpan.FromSeconds(30));
        cluster.Add(work, TimeSpan.FromMinutes(1), retryBatch: 2);
        cluster.Add(work, TimeSpan.FromMinutes(1), retryBatch: 2);
        await cluster.StartAllAsync(token);
        await cluster.HolderAfterOneLiveEventAsync(token);

        await Task.WhenAll(cluster.DeadLetterAsync(8, token), cluster.AppendAsync(8, token));
        await cluster.Done.Reached(17);

        var calls = cluster.Calls;
        Assert.Equal(17, calls.Count);
        Assert.Equal(8, calls.Count(c => c.Retry));
        Assert.All(calls.Zip(calls.Skip(1)), pair => Assert.True(
            pair.First.End <= pair.Second.Start,
            $"{(pair.First.Retry ? "retry" : "live")} {pair.First.Start:HH:mm:ss.fff}-{pair.First.End:HH:mm:ss.fff} overlaps " +
            $"{(pair.Second.Retry ? "retry" : "live")} {pair.Second.Start:HH:mm:ss.fff}-{pair.Second.End:HH:mm:ss.fff}"));
        Assert.Single(calls.Select(c => c.Replica).Distinct());
    }

    [Fact]
    public async Task WhenTheLeaseMoves_TheNewHolderRetries_AndRunsAnOldClaimOnlyAfterItExpires()
    {
        var token = TestContext.Current.CancellationToken;
        var claimLease = TimeSpan.FromSeconds(2);
        await using var cluster = new Cluster(TimeSpan.FromSeconds(1));
        var first = cluster.Add(TimeSpan.Zero, claimLease, retryBatch: 1);
        await cluster.StartAllAsync(token);
        await cluster.HolderAfterOneLiveEventAsync(token);
        var second = cluster.Add(TimeSpan.Zero, claimLease);
        await cluster.StartAsync(second, token);

        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledWithTheLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Processor.BlockRetries = async ct =>
        {
            blocked.TrySetResult();
            try
            {
                await new TaskCompletionSource().Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                cancelledWithTheLease.TrySetResult();
                throw;
            }
        };
        var ids = await cluster.DeadLetterAsync(3, token);
        await blocked.Task.WaitAsync(Patience, token);
        var stuck = Assert.Single(first.Processor.RetriesStarted);
        var stuckClaimExpires = (await cluster.DeadLetters.GetAsync(ProcessorId, cancellationToken: token))
            .Single(e => e.EventId == stuck).ClaimExpiresAt!.Value.UtcDateTime;

        await first.Group.StopAsync(CancellationToken.None);
        await cancelledWithTheLease.Task.WaitAsync(Patience, token);
        await second.Processor.Done.Reached(3);

        Assert.Equal(ids.Order(), second.Processor.Calls.Select(c => c.EventId).Order());
        Assert.True(second.Processor.Calls.Single(c => c.EventId == stuck).Start >= stuckClaimExpires,
            "the old holder's claim is not run again before its claim lease expires");
        Assert.Single(first.Processor.RetriesStarted);
        Assert.Equal(1, cluster.DeadLetters.ClaimedBy(first.Name).Value);
        Assert.Equal(0, await cluster.DeadLetters.CountAsync(ProcessorId, token));
    }

    [EventType("retry-lease-wired")]
    public sealed record Wired(string Id) : IEvent;

    public sealed class WiredCalls
    {
        public ConcurrentQueue<string> Ids { get; } = new();
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WiredHandler(WiredCalls calls)
    {
        public Task Run(string id)
        {
            calls.Ids.Enqueue(id);
            calls.First.TrySetResult();
            return Task.CompletedTask;
        }
    }

    public enum LeaseState { Off, HeldHere, HeldElsewhere }

    [Theory]
    [InlineData(LeaseState.Off)]
    [InlineData(LeaseState.HeldHere)]
    [InlineData(LeaseState.HeldElsewhere)]
    public async Task Registered_RetriesRunOnlyWhereTheLeaseIsHeld(LeaseState leases)
    {
        var token = TestContext.Current.CancellationToken;
        var calls = new WiredCalls();
        var services = new ServiceCollection();
        services.AddTenancy();
        services.AddSingleton(calls);
        services.AddAlberto("test", builder => builder
            .WithInMemory()
            .WithControlLoop(o => o with
            {
                Leases = o.Leases with { Enabled = leases != LeaseState.Off, ReplicaId = "me" },
                DeadLetterRetry = o.DeadLetterRetry with { PollingInterval = TimeSpan.FromMilliseconds(20) },
            })
            .React<WiredHandler>("wired", on => on.On<Wired>((h, e, _, _) => h.Run(e.Id))));
        await using var provider = services.BuildServiceProvider();
        var processorId = provider.GetKeyedServices<IEventProcessor>("test").Single().ProcessorId;

        if (leases == LeaseState.HeldElsewhere)
            Assert.NotNull(await provider.GetRequiredKeyedService<IProcessorLeaseManager>("test")
                .TryAcquireAsync("test", processorId, "someone-else", token));

        var store = provider.GetRequiredKeyedService<IDeadLetterStore>("test");
        await store.StoreAsync(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = Guid.NewGuid(),
            EventType = "retry-lease-wired",
            EventData = JsonSerializer.Serialize(new Wired("x")),
            ErrorMessage = "failed",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            RetryRequested = true,
            TenantId = "t1",
        }, token);

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(token);
        try
        {
            if (leases == LeaseState.HeldElsewhere)
                await Assert.ThrowsAsync<TimeoutException>(() => calls.First.Task.WaitAsync(TimeSpan.FromSeconds(1), token));
            else
                await calls.First.Task.WaitAsync(Patience, token);
        }
        finally
        {
            foreach (var service in Enumerable.Reverse(hosted))
                await service.StopAsync(CancellationToken.None);
        }

        var entries = await store.GetAsync(processorId, cancellationToken: token);
        if (leases == LeaseState.HeldElsewhere)
        {
            Assert.Empty(calls.Ids);
            Assert.Null(Assert.Single(entries).ClaimedBy);
        }
        else
        {
            Assert.Equal(["x"], calls.Ids);
            Assert.Empty(entries);
        }
    }
}
