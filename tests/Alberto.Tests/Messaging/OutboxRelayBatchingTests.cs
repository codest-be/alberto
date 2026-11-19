using Alberto.InMemory;
using Alberto.Testing;
using Alberto.Messaging;
using Xunit;

namespace Alberto.Tests.Messaging;

/// <summary>
/// The relay records a claimed batch's failures in one call when the store can, falls back to
/// one call per entry when it cannot, and wakes on a local enqueue instead of waiting out its
/// idle delay.
/// </summary>
public class OutboxRelayBatchingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Fails every message of type "bad".</summary>
    private sealed class SelectiveTransport : IMessageTransport
    {
        public Task PublishAsync(ExternalMessage message, CancellationToken cancellationToken) =>
            message.MessageType == "bad"
                ? throw new InvalidOperationException($"broker refused {message.MessageType}")
                : Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Counts the per-entry failure marks; optionally also implements the batch capability.</summary>
    private class CountingStore(InMemoryOutboxStore inner) : IOutboxStore
    {
        public int SingleFailureMarks;

        public Task InsertAsync(OutboxEntry entry, CancellationToken cancellationToken = default) =>
            inner.InsertAsync(entry, cancellationToken);
        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit, TimeSpan claimLease, string claimedBy, CancellationToken cancellationToken = default) =>
            inner.ClaimPendingAsync(limit, claimLease, claimedBy, cancellationToken);
        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken cancellationToken = default) =>
            inner.MarkDeliveredAsync(claim, cancellationToken);
        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken cancellationToken = default) =>
            inner.MarkFailedAsync(claim, error, cancellationToken);
        public Task<bool> MarkAttemptFailedAsync(
            OutboxClaim claim, string error, int maxRetries, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SingleFailureMarks);
            return inner.MarkAttemptFailedAsync(claim, error, maxRetries, cancellationToken);
        }
        public Task RetryFailedAsync(string? messageType = null, CancellationToken cancellationToken = default) =>
            inner.RetryFailedAsync(messageType, cancellationToken);
        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken cancellationToken = default) =>
            inner.PurgeDeliveredAsync(before, cancellationToken);
    }

    private sealed class BatchingStore(InMemoryOutboxStore inner) : CountingStore(inner), IOutboxFailureBatchStore
    {
        private readonly InMemoryOutboxStore _inner = inner;
        public List<int> Batches { get; } = [];

        public async Task<IReadOnlySet<Guid>> MarkAttemptsFailedAsync(
            IReadOnlyList<OutboxFailure> failures, int maxRetries, CancellationToken cancellationToken)
        {
            lock (Batches) Batches.Add(failures.Count);
            var marked = new HashSet<Guid>();
            foreach (var (claim, error) in failures)
                if (await _inner.MarkAttemptFailedAsync(claim, error, maxRetries, cancellationToken))
                    marked.Add(claim.Entry.Id);
            return marked;
        }
    }

    private static OutboxEntry Entry(string messageType) =>
        new(Guid.NewGuid(), Guid.NewGuid(), messageType, "1", "{}", new Dictionary<string, string>(),
            OutboxEntryStatus.Pending, 0, null, DateTimeOffset.UtcNow, null);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return condition();
    }

    private static async Task StopAsync(OutboxRelay relay)
    {
        await relay.StopAsync(TestContext.Current.CancellationToken);
        try { await relay.ExecuteTask!; }
        catch (OperationCanceledException) { }
        relay.Dispose();
    }

    private static async Task<InMemoryOutboxStore> RunMixedBatchAsync(Func<InMemoryOutboxStore, CountingStore> wrap)
    {
        var inner = new InMemoryOutboxStore();
        foreach (var type in new[] { "good", "bad", "good", "bad", "bad" })
            await inner.InsertAsync(Entry(type), TestContext.Current.CancellationToken);
        var relay = new OutboxRelay(
            wrap(inner), new SelectiveTransport(), pollingInterval: TimeSpan.FromHours(1), maxRetries: 3);

        await relay.StartAsync(TestContext.Current.CancellationToken);
        // One claim, then the hour-long idle wait: everything settles after the first batch.
        Assert.True(await WaitUntilAsync(() => inner.Entries.All(e =>
            e.MessageType == "good" ? e.Status == OutboxEntryStatus.Delivered : e.RetryCount == 1)));
        await StopAsync(relay);
        return inner;
    }

    [Fact]
    public async Task MixedBatch_RecordsAllFailuresInOneCall_WhenTheStoreCanBatch()
    {
        BatchingStore? store = null;
        var inner = await RunMixedBatchAsync(i => store = new BatchingStore(i));

        Assert.Equal([3], store!.Batches);
        Assert.Equal(0, store.SingleFailureMarks);
        AssertMixedOutcome(inner);
    }

    [Fact]
    public async Task MixedBatch_FallsBackToOneMarkPerFailure_WhenTheStoreCannotBatch()
    {
        CountingStore? store = null;
        var inner = await RunMixedBatchAsync(i => store = new CountingStore(i));

        Assert.Equal(3, store!.SingleFailureMarks);
        AssertMixedOutcome(inner);
    }

    private static void AssertMixedOutcome(InMemoryOutboxStore inner)
    {
        Assert.All(inner.Entries.Where(e => e.MessageType == "good"),
            e => Assert.Equal(OutboxEntryStatus.Delivered, e.Status));
        Assert.All(inner.Entries.Where(e => e.MessageType == "bad"), e =>
        {
            Assert.Equal(OutboxEntryStatus.Pending, e.Status);
            Assert.Equal(1, e.RetryCount);
            Assert.Equal("broker refused bad", e.LastError);
        });
    }

    [Fact]
    public async Task LocalEnqueue_IsDeliveredWithoutWaitingOutTheIdleDelay()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();
        var wake = new WakeSignal();
        var relay = new OutboxRelay(
            store,
            new OutboxTransportLifecycle(transport).CreateLease(),
            pollingInterval: TimeSpan.FromHours(1),
            wake: wake,
            idleCeiling: TimeSpan.FromHours(1));

        await relay.StartAsync(TestContext.Current.CancellationToken);
        // Let the first (empty) poll happen, so the relay is in its hour-long idle wait.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await store.InsertAsync(Entry("good"), TestContext.Current.CancellationToken);
        wake.Notify();

        Assert.True(await WaitUntilAsync(() => transport.Published.Count == 1));
        await StopAsync(relay);
    }

    [Theory]
    [InlineData(1_000, 1, 1_000)]
    [InlineData(1_000, 2, 2_000)]
    [InlineData(1_000, 3, 4_000)]
    [InlineData(1_000, 4, 5_000)]
    [InlineData(1_000, 100, 5_000)]
    [InlineData(10_000, 3, 10_000)]
    public void IdleBackoff_DoublesUpToTheIdleCeiling_OrThePollingIntervalIfLonger(
        int pollingMs, int idleStreak, int expectedMs)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            OutboxRelay.Backoff(TimeSpan.FromMilliseconds(pollingMs), idleStreak, OutboxRelay.DefaultIdleCeiling));
    }
}
