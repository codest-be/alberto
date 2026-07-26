using Alberto.Dcb.Messaging;
using Xunit;

namespace Alberto.Dcb.Tests.Messaging;

/// <summary>
/// Tests that OutboxRelay picks up pending entries and publishes them via the transport.
/// </summary>
public class OutboxRelayTests
{
    #region In-Memory Outbox Store

    private sealed class InMemoryOutboxStore : IOutboxStore
    {
        private readonly List<OutboxEntry> _entries = new();
        private readonly Dictionary<Guid, OutboxClaim> _claims = new();

        public IReadOnlyList<OutboxEntry> Entries => _entries;

        public void Seed(OutboxEntry entry) => _entries.Add(entry);

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit,
            TimeSpan claimLease,
            string claimedBy,
            CancellationToken ct = default)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(claimLease);
            var pending = _entries
                .Where(e => e.Status == OutboxEntryStatus.Pending)
                .Take(limit)
                .ToList();
            var claims = new List<OutboxClaim>(pending.Count);
            foreach (var entry in pending)
            {
                var index = _entries.FindIndex(e => e.Id == entry.Id);
                var processing = entry with { Status = OutboxEntryStatus.Processing };
                _entries[index] = processing;
                var claim = new OutboxClaim(processing, Guid.NewGuid(), expiresAt);
                _claims[entry.Id] = claim;
                claims.Add(claim);
            }
            return Task.FromResult<IReadOnlyList<OutboxClaim>>(claims);
        }

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
        {
            if (!_claims.TryGetValue(claim.Entry.Id, out var current)
                || current.Token != claim.Token
                || current.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(false);

            var idx = _entries.FindIndex(e => e.Id == claim.Entry.Id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with { Status = OutboxEntryStatus.Delivered, DeliveredAt = DateTimeOffset.UtcNow };
            _claims.Remove(claim.Entry.Id);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
        {
            if (!_claims.TryGetValue(claim.Entry.Id, out var current)
                || current.Token != claim.Token
                || current.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(false);

            var idx = _entries.FindIndex(e => e.Id == claim.Entry.Id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with
                {
                    Status = OutboxEntryStatus.Failed,
                    RetryCount = _entries[idx].RetryCount + 1,
                    LastError = error
                };
            _claims.Remove(claim.Entry.Id);
            return Task.FromResult(true);
        }

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Status == OutboxEntryStatus.Failed &&
                    (messageType is null || _entries[i].MessageType == messageType))
                {
                    _entries[i] = _entries[i] with { Status = OutboxEntryStatus.Pending, RetryCount = 0, LastError = null };
                    _claims.Remove(_entries[i].Id);
                }
            }
            return Task.CompletedTask;
        }

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.Status == OutboxEntryStatus.Delivered && e.DeliveredAt < before);
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Poll-Signaling Outbox Store

    /// <summary>
    /// Decorator over <see cref="IOutboxStore"/> that signals <see cref="WhenPolled"/>
    /// at the start of the second <see cref="ClaimPendingAsync"/> call. By that point
    /// the first full poll cycle — claiming, publishing, and marking delivered/failed —
    /// is complete. Tests await this instead of a wall-clock sleep.
    /// Uses composition so <see cref="InMemoryOutboxStore"/> callers keep direct access
    /// to <c>Seed</c> and <c>Entries</c> through their own reference.
    /// </summary>
    private sealed class PollSignalingOutboxStore : IOutboxStore
    {
        private readonly IOutboxStore _inner;
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pollCount;

        public PollSignalingOutboxStore(IOutboxStore inner) => _inner = inner;

        /// <summary>
        /// Completes when the relay begins its second <see cref="ClaimPendingAsync"/> call,
        /// confirming the first batch was fully processed.
        /// </summary>
        public Task WhenPolled => _tcs.Task;

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
            => _inner.InsertAsync(entry, ct);

        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _pollCount) >= 2)
                _tcs.TrySetResult();
            return _inner.ClaimPendingAsync(limit, claimLease, claimedBy, ct);
        }

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
            => _inner.MarkDeliveredAsync(claim, ct);

        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default)
            => _inner.MarkFailedAsync(claim, error, ct);

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
            => _inner.RetryFailedAsync(messageType, ct);

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
            => _inner.PurgeDeliveredAsync(before, ct);
    }

    #endregion

    #region Failing Transport

    private sealed class FailingTransport : IMessageTransport
    {
        public Task PublishAsync(ExternalMessage message, CancellationToken ct)
            => throw new InvalidOperationException("Transport failure");

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    #endregion

    #region Helpers

    private static OutboxEntry MakePendingEntry(string messageType = "order.placed") =>
        new(
            Id: Guid.NewGuid(),
            SourceEventId: Guid.NewGuid(),
            MessageType: messageType,
            Version: "1",
            Payload: "{}",
            Metadata: new Dictionary<string, string>(),
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);

    /// <summary>
    /// Runs the relay for one cycle, cancelling after the first poll completes.
    /// Uses a very short polling interval so we don't hang in tests.
    /// </summary>
    private static async Task RunRelayCycleAsync(
        IOutboxStore store,
        IMessageTransport transport,
        int batchSize = 100)
    {
        using var cts = new CancellationTokenSource();
        var signal = new PollSignalingOutboxStore(store);
        var relay = new OutboxRelay(signal, transport, pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: batchSize);

        // Start relay and wait until it begins its second poll. By that time the first
        // batch — ClaimPendingAsync → PublishAsync → MarkDeliveredAsync/MarkFailedAsync
        // — is fully committed to the in-memory store and the test can assert safely.
        // WaitAsync(5s) makes a hang fail loudly rather than time out silently.
        var relayTask = relay.StartAsync(cts.Token);
        await signal.WhenPolled.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        try { await relayTask; } catch (OperationCanceledException) { }
    }

    #endregion

    #region Tests

    [Fact]
    public async Task Relay_PublishesPendingEntries()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        store.Seed(MakePendingEntry("order.placed"));
        store.Seed(MakePendingEntry("order.shipped"));

        await RunRelayCycleAsync(store, transport);

        Assert.Equal(2, transport.Published.Count);
        Assert.All(store.Entries, e => Assert.Equal(OutboxEntryStatus.Delivered, e.Status));
    }

    [Fact]
    public async Task Relay_MarksEntryDelivered_AfterSuccessfulPublish()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        store.Seed(MakePendingEntry());

        await RunRelayCycleAsync(store, transport);

        Assert.Single(store.Entries);
        Assert.Equal(OutboxEntryStatus.Delivered, store.Entries[0].Status);
        Assert.NotNull(store.Entries[0].DeliveredAt);
    }

    [Fact]
    public async Task Relay_MarksEntryFailed_WhenTransportThrows()
    {
        var store = new InMemoryOutboxStore();
        var transport = new FailingTransport();

        store.Seed(MakePendingEntry());

        await RunRelayCycleAsync(store, transport);

        Assert.Single(store.Entries);
        Assert.Equal(OutboxEntryStatus.Failed, store.Entries[0].Status);
        Assert.Equal(1, store.Entries[0].RetryCount);
        Assert.Equal("Transport failure", store.Entries[0].LastError);
    }

    [Fact]
    public async Task Relay_EmptyOutbox_DoesNotPublishAnything()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        await RunRelayCycleAsync(store, transport);

        Assert.Empty(transport.Published);
    }

    [Fact]
    public async Task Relay_PassesCorrectMessageToTransport()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        var entry = MakePendingEntry("order.placed") with
        {
            Version = "2",
            Payload = """{"orderId":"test"}""",
            Metadata = new Dictionary<string, string> { ["source"] = "test" }
        };
        store.Seed(entry);

        await RunRelayCycleAsync(store, transport);

        Assert.Single(transport.Published);
        var msg = transport.Published.First();
        Assert.Equal("order.placed", msg.MessageType);
        Assert.Equal("2", msg.Version);
        Assert.Equal("""{"orderId":"test"}""", msg.Payload);
        Assert.Equal("test", msg.Metadata["source"]);
    }

    #endregion
}
