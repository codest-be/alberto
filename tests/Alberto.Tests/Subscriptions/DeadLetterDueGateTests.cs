using System.Collections.Concurrent;
using System.Threading.Channels;
using Alberto.DeadLetters;
using Alberto.InMemory;
using Alberto.Subscriptions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// A module's retry loops behind a <see cref="DeadLetterDueGate"/>: one due-probe read per interval
/// replaces one claim per processor per interval, and only the processors with due work claim.
/// </summary>
public sealed class DeadLetterDueGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly string[] Processors = ["proc-a", "proc-b", "proc-c"];

    [Fact]
    public async Task IdleModule_ReadsOncePerInterval_AndNeverClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new ProbingStore();
        await using var group = Build(store, time, out _);

        await group.StartAsync(ct);
        var first = await store.Probes.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);

        // The gate arms its delay only after the probe returns, so keep stepping the clock by one
        // interval until the next probe shows up rather than racing a single Advance.
        DateTimeOffset second = default;
        for (var attempt = 0; attempt < 50 && !store.Probes.Reader.TryRead(out second); attempt++)
        {
            time.Advance(Interval);
            await Task.Delay(20, ct);
        }

        Assert.Equal(Interval, second - first);
        Assert.False(store.Probes.Reader.TryRead(out _));
        Assert.Empty(store.Claims);
    }

    [Fact]
    public async Task DueProcessor_IsClaimedAndRetried_OthersAreNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new ProbingStore();
        await store.StoreAsync(Entry("proc-b"), ct);
        await using var group = Build(store, time, out var processed);

        await group.StartAsync(ct);

        var id = await processed.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("proc-b", id);
        Assert.Equal(["proc-b"], store.Claims.Keys);
    }

    [Fact]
    public async Task FailingProbe_FallsBackToClaimingEveryProcessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new ProbingStore { ProbeFails = true };
        await using var group = Build(store, time, out _);

        await group.StartAsync(ct);

        await store.AllClaimed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(Processors.Order(), store.Claims.Keys.Order());
    }

    private static DeadLetterRetryLoopGroup Build(
        ProbingStore store, TimeProvider time, out Channel<string> processed)
    {
        var channel = Channel.CreateUnbounded<string>();
        processed = channel;
        var gate = new DeadLetterDueGate(store, Processors, Interval, time);
        var loops = Processors
            .Select(id => new DeadLetterRetryLoop(
                new SignalingProcessor(id, channel.Writer), store,
                pollingInterval: Interval, timeProvider: time, gate: gate))
            .ToList();
        return new DeadLetterRetryLoopGroup(loops, gate);
    }

    private static DeadLetterEntry Entry(string processorId) => new()
    {
        Id = Guid.NewGuid(),
        ProcessorId = processorId,
        EventId = Guid.NewGuid(),
        EventType = "test-event",
        EventData = "{}",
        ErrorMessage = "previous failure",
        StackTrace = null,
        AttemptCount = 1,
        FailedAt = DateTimeOffset.UtcNow,
        GlobalPosition = 1,
        RetryRequested = true,
    };

    private sealed class SignalingProcessor(string processorId, ChannelWriter<string> processed) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
        {
            processed.TryWrite(ProcessorId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory store that also answers the due probe from its own entries, recording each probe
    /// and each claim per processor.
    /// </summary>
    private sealed class ProbingStore : IClaimableDeadLetterStore, IDeadLetterDueProbe
    {
        private readonly InMemoryDeadLetterStore _inner = new();

        public bool ProbeFails { get; init; }
        public Channel<DateTimeOffset> Probes { get; } = Channel.CreateUnbounded<DateTimeOffset>();
        public ConcurrentDictionary<string, int> Claims { get; } = new();
        public TaskCompletionSource AllClaimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlySet<string>> FindProcessorsWithDueAsync(
            IReadOnlyCollection<string> processorIds, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Probes.Writer.TryWrite(now);
            if (ProbeFails)
                throw new InvalidOperationException("probe down");

            var due = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in processorIds)
                if ((await _inner.GetAsync(id, cancellationToken: cancellationToken)).Any(e => e.RetryRequested))
                    due.Add(id);
            return due;
        }

        public Task<IReadOnlyList<DeadLetterClaim>> ClaimDueAsync(
            string processorId, int batchSize, TimeSpan leaseDuration, string claimedBy,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Claims.AddOrUpdate(processorId, 1, (_, n) => n + 1);
            if (Claims.Count == Processors.Length)
                AllClaimed.TrySetResult();
            return _inner.ClaimDueAsync(processorId, batchSize, leaseDuration, claimedBy, now, cancellationToken);
        }

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
            => _inner.StoreAsync(entry, cancellationToken);

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
            string processorId, string? tenantId = null, int limit = 100, CancellationToken cancellationToken = default)
            => _inner.GetAsync(processorId, tenantId, limit, cancellationToken);

        public Task<int> CountAsync(string processorId, CancellationToken cancellationToken = default)
            => _inner.CountAsync(processorId, cancellationToken);

        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken cancellationToken = default)
            => _inner.CompleteRetryAsync(claim, cancellationToken);

        public Task ClearAsync(string processorId, CancellationToken cancellationToken = default)
            => _inner.ClearAsync(processorId, cancellationToken);

        public Task MarkForRetryAsync(string processorId, CancellationToken cancellationToken = default)
            => _inner.MarkForRetryAsync(processorId, cancellationToken);

        public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId, int batchSize, TimeSpan leaseDuration,
            string claimedBy, CancellationToken cancellationToken = default)
            => _inner.ClaimRetryRequestedAsync(processorId, batchSize, leaseDuration, claimedBy, cancellationToken);

        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken cancellationToken = default)
            => _inner.AbandonRetryAsync(claim, cancellationToken);
    }
}
