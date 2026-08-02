using Alberto.InMemory;
using Alberto.Messaging;
using Alberto.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Messaging;

/// <summary>
/// Tests that OutboxRelay picks up pending entries and publishes them via the transport.
/// </summary>
public class OutboxRelayTests
{
    #region Poll-Signaling Outbox Store

    /// <summary>
    /// Decorator over <see cref="IOutboxStore"/> that signals <see cref="WhenPolled"/> at
    /// the start of the second <see cref="ClaimPendingAsync"/> call. By that point the first
    /// full poll cycle — claiming, publishing, and marking delivered/failed — is complete.
    /// Tests await this instead of a wall-clock sleep. <see cref="WhenFirstPolled"/> fires on
    /// the very first call, which is sufficient for tests that just need to know the relay's
    /// loop is running before they stop it.
    /// Uses composition so callers keep direct access to <c>Entries</c> through their own
    /// reference to the <see cref="InMemoryOutboxStore"/> passed as the constructor's inner store.
    /// </summary>
    private sealed class PollSignalingOutboxStore : IOutboxStore
    {
        private readonly IOutboxStore _inner;
        private readonly Action? _onClaim;
        private readonly TaskCompletionSource _firstPoll =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pollCount;

        public PollSignalingOutboxStore(IOutboxStore inner, Action? onClaim = null)
        {
            _inner = inner;
            _onClaim = onClaim;
        }

        /// <summary>
        /// Completes as soon as <see cref="ClaimPendingAsync"/> is first called, confirming
        /// the relay's polling loop is running.
        /// </summary>
        public Task WhenFirstPolled => _firstPoll.Task;

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
            _firstPoll.TrySetResult();
            _onClaim?.Invoke();
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

    #region Lifecycle Test Doubles

    private sealed class FailingTransport : IMessageTransport
    {
        public Task PublishAsync(ExternalMessage message, CancellationToken ct)
            => throw new InvalidOperationException("Transport failure");

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LifecycleTransport : IMessageTransport, IDisposable, IAsyncDisposable
    {
        private readonly IMessageTransport _inner;
        private readonly Func<CancellationToken, Task>? _start;
        private readonly Func<CancellationToken, Task>? _stop;
        private readonly Action<string>? _record;
        private int _starts;
        private int _stops;
        private int _publishes;
        private int _disposals;

        public LifecycleTransport(
            IMessageTransport? inner = null,
            Func<CancellationToken, Task>? start = null,
            Func<CancellationToken, Task>? stop = null,
            Action<string>? record = null)
        {
            _inner = inner ?? new InMemoryTransport();
            _start = start;
            _stop = stop;
            _record = record;
        }

        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public int Publishes => Volatile.Read(ref _publishes);
        public int Disposals => Volatile.Read(ref _disposals);

        public Task PublishAsync(ExternalMessage message, CancellationToken ct)
        {
            Interlocked.Increment(ref _publishes);
            _record?.Invoke("publish");
            return _inner.PublishAsync(message, ct);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            _record?.Invoke("start");

            if (_start is not null)
                await _start(ct);
            else
                await _inner.StartAsync(ct);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            _record?.Invoke("stop");

            if (_stop is not null)
                await _stop(ct);
            else
                await _inner.StopAsync(ct);
        }

        public void Dispose() => Interlocked.Increment(ref _disposals);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledClaimOutboxStore : IOutboxStore
    {
        private readonly TaskCompletionSource<IReadOnlyList<OutboxClaim>> _claim =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _whenClaimed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenClaimed => _whenClaimed.Task;

        public void FailClaim(Exception exception) => _claim.TrySetException(exception);

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit,
            TimeSpan claimLease,
            string claimedBy,
            CancellationToken ct = default)
        {
            _whenClaimed.TrySetResult();
            return await _claim.Task.WaitAsync(ct);
        }

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> MarkFailedAsync(
            OutboxClaim claim,
            string error,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default) =>
            Task.CompletedTask;
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
    /// Runs the relay for one cycle, then asks the hosted service to stop.
    /// Uses a very short polling interval so we don't hang in tests.
    /// </summary>
    private static async Task RunRelayCycleAsync(
        IOutboxStore store,
        IMessageTransport transport,
        int batchSize = 100,
        Action? onClaim = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signal = new PollSignalingOutboxStore(store, onClaim);
        var relay = new OutboxRelay(signal, transport, pollingInterval: TimeSpan.FromMilliseconds(10), batchSize: batchSize);

        // Start relay and wait until it begins its second poll. By that time the first
        // batch — ClaimPendingAsync → PublishAsync → MarkDeliveredAsync/MarkFailedAsync
        // — is fully committed to the in-memory store and the test can assert safely.
        // WaitAsync(5s) makes a hang fail loudly rather than time out silently.
        await relay.StartAsync(timeout.Token);
        await signal.WhenPolled.WaitAsync(TimeSpan.FromSeconds(5));
        await relay.StopAsync(timeout.Token);
        var executeTask = relay.ExecuteTask!;
        try
        {
            await executeTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (executeTask.IsCanceled)
        {
            // Normal BackgroundService shutdown keeps ExecuteTask cancelled after cleanup.
        }
        relay.Dispose();
    }

    #endregion

    #region Tests

    [Fact]
    public async Task Relay_PublishesPendingEntries()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        await store.InsertAsync(MakePendingEntry("order.placed"));
        await store.InsertAsync(MakePendingEntry("order.shipped"));

        await RunRelayCycleAsync(store, transport);

        Assert.Equal(2, transport.Published.Count);
        Assert.All(store.Entries, e => Assert.Equal(OutboxEntryStatus.Delivered, e.Status));
    }

    [Fact]
    public async Task Relay_MarksEntryDelivered_AfterSuccessfulPublish()
    {
        var store = new InMemoryOutboxStore();
        var transport = new InMemoryTransport();

        await store.InsertAsync(MakePendingEntry());

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

        await store.InsertAsync(MakePendingEntry());

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
        await store.InsertAsync(entry);

        await RunRelayCycleAsync(store, transport);

        Assert.Single(transport.Published);
        var msg = transport.Published.First();
        Assert.Equal("order.placed", msg.MessageType);
        Assert.Equal("2", msg.Version);
        Assert.Equal("""{"orderId":"test"}""", msg.Payload);
        Assert.Equal("test", msg.Metadata["source"]);
    }

    [Fact]
    public async Task Relay_Starts_And_Stops_Transport_Exactly_Once()
    {
        var store = new InMemoryOutboxStore();
        var transport = new LifecycleTransport();

        await RunRelayCycleAsync(store, transport);

        Assert.Equal(1, transport.Starts);
        Assert.Equal(1, transport.Stops);
    }

    [Fact]
    public async Task Relay_StartsTransport_BeforeClaimingOrPublishing()
    {
        var calls = new List<string>();
        var gate = new object();
        void Record(string call)
        {
            lock (gate)
                calls.Add(call);
        }

        var store = new InMemoryOutboxStore();
        var transport = new LifecycleTransport(record: Record);
        await store.InsertAsync(MakePendingEntry());

        await RunRelayCycleAsync(store, transport, onClaim: () => Record("claim"));

        Assert.True(calls.IndexOf("start") < calls.IndexOf("claim"));
        Assert.True(calls.IndexOf("claim") < calls.IndexOf("publish"));
        Assert.True(calls.IndexOf("publish") < calls.IndexOf("stop"));
    }

    [Fact]
    public async Task Relay_StopsTransport_WhenThePollingLoopFails()
    {
        var store = new ControlledClaimOutboxStore();
        var loopFailure = new InvalidOperationException("store failure");
        var transport = new LifecycleTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new OutboxRelay(store, transport);

        await relay.StartAsync(timeout.Token);
        await store.WhenClaimed.WaitAsync(timeout.Token);
        store.FailClaim(loopFailure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.ExecuteTask!.WaitAsync(timeout.Token));

        Assert.Same(loopFailure, exception);
        Assert.Equal(1, transport.Stops);
        relay.Dispose();
    }

    [Fact]
    public async Task RepeatedHostStopAndDispose_DoNotStopOrDisposeTransportTwice()
    {
        var inner = new InMemoryOutboxStore();
        var store = new PollSignalingOutboxStore(inner);
        var transport = new LifecycleTransport();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new OutboxRelay(store, transport);

        await relay.StartAsync(timeout.Token);
        await store.WhenFirstPolled.WaitAsync(timeout.Token);
        await relay.StopAsync(timeout.Token);
        await relay.StopAsync(timeout.Token);
        relay.Dispose();
        relay.Dispose();

        Assert.Equal(1, transport.Stops);
        Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task StartupFailure_StopsPartialTransport_WithoutClaimingOrPublishing()
    {
        var startupFailure = new InvalidOperationException("startup failure");
        var store = new InMemoryOutboxStore();
        var transport = new LifecycleTransport(
            start: _ => Task.FromException(startupFailure));
        var relay = new OutboxRelay(store, transport);

        await relay.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.ExecuteTask!);

        Assert.Same(startupFailure, exception);
        Assert.Equal(1, transport.Starts);
        Assert.Equal(1, transport.Stops);
        // transport.Publishes == 0 already confirms no claim was attempted
        Assert.Equal(0, transport.Publishes);
        relay.Dispose();
    }

    [Fact]
    public async Task StartupAndCleanupFailure_PreservesStartupFailureAndAttachesCleanupFailure()
    {
        var startupFailure = new InvalidOperationException("startup failure");
        var cleanupFailure = new ApplicationException("cleanup failure");
        var transport = new LifecycleTransport(
            start: _ => Task.FromException(startupFailure),
            stop: _ => Task.FromException(cleanupFailure));
        var relay = new OutboxRelay(new InMemoryOutboxStore(), transport);

        await relay.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.ExecuteTask!);

        Assert.Same(startupFailure, exception);
        Assert.Same(
            cleanupFailure,
            exception.Data[OutboxTransportLifecycle.CleanupFailureDataKey]);
        Assert.Equal(1, transport.Stops);
        relay.Dispose();
    }

    [Fact]
    public async Task CleanupFailure_AfterNormalCancellation_IsObservable()
    {
        var cleanupFailure = new InvalidOperationException("cleanup failure");
        var transport = new LifecycleTransport(
            stop: _ => Task.FromException(cleanupFailure));
        var inner = new InMemoryOutboxStore();
        var store = new PollSignalingOutboxStore(inner);
        var relay = new OutboxRelay(store, transport);

        await relay.StartAsync(TestContext.Current.CancellationToken);
        await store.WhenFirstPolled.WaitAsync(TestContext.Current.CancellationToken);
        await relay.StopAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.ExecuteTask!);
        Assert.Same(cleanupFailure, exception);
        Assert.Equal(1, transport.Stops);
        relay.Dispose();
    }

    [Fact]
    public async Task LoopAndCleanupFailure_PreservesLoopFailureAndAttachesCleanupFailure()
    {
        var store = new ControlledClaimOutboxStore();
        var loopFailure = new InvalidOperationException("store failure");
        var cleanupFailure = new InvalidOperationException("cleanup failure");
        var transport = new LifecycleTransport(
            stop: _ => Task.FromException(cleanupFailure));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = new OutboxRelay(store, transport);

        await relay.StartAsync(timeout.Token);
        await store.WhenClaimed.WaitAsync(timeout.Token);
        store.FailClaim(loopFailure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => relay.ExecuteTask!.WaitAsync(timeout.Token));

        Assert.Same(loopFailure, exception);
        Assert.Same(
            cleanupFailure,
            exception.Data[OutboxTransportLifecycle.CleanupFailureDataKey]);
        Assert.Equal(1, transport.Stops);
        relay.Dispose();
    }

    [Fact]
    public async Task SharedTransportAcrossOutboxRegistrations_HasOneLifecycleOwner()
    {
        var transport = new LifecycleTransport();
        var services = new ServiceCollection();
        var ordersInner = new InMemoryOutboxStore();
        var ordersStore = new PollSignalingOutboxStore(ordersInner);
        var paymentsInner = new InMemoryOutboxStore();
        var paymentsStore = new PollSignalingOutboxStore(paymentsInner);
        services.AddAlberto("orders", builder => builder
            .WithInMemory()
            .WithOutbox(_ => { }, ordersStore, transport));
        services.AddAlberto("payments", builder => builder
            .WithInMemory()
            .WithOutbox(_ => { }, paymentsStore, transport));

        await using (var provider = services.BuildServiceProvider())
        {
            var relays = provider
                .GetServices<IHostedService>()
                .OfType<OutboxRelay>()
                .ToArray();
            Assert.Equal(2, relays.Length);

            foreach (var relay in relays)
                await relay.StartAsync(TestContext.Current.CancellationToken);

            await Task.WhenAll(ordersStore.WhenFirstPolled, paymentsStore.WhenFirstPolled)
                .WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, transport.Starts);

            await relays[0].StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, transport.Stops);

            await relays[1].StopAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, transport.Stops);
        }

        Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task CleanupThatIgnoresCancellation_IsBoundedByLifecycleTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupNeverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new LifecycleTransport(stop: ct =>
        {
            cleanupStarted.TrySetResult();
            return cleanupNeverCompletes.Task;
        });
        var lifecycle = new OutboxTransportLifecycle(
            transport,
            TimeSpan.FromMinutes(1),
            timeProvider);
        var inner = new InMemoryOutboxStore();
        var store = new PollSignalingOutboxStore(inner);
        var relay = new OutboxRelay(
            store,
            lifecycle.CreateLease(),
            pollingInterval: TimeSpan.FromMinutes(1));

        await relay.StartAsync(TestContext.Current.CancellationToken);
        await store.WhenFirstPolled.WaitAsync(TestContext.Current.CancellationToken);
        var stopTask = relay.StopAsync(TestContext.Current.CancellationToken);
        await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await stopTask;

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => relay.ExecuteTask!);
        Assert.Contains("00:01:00", exception.Message);
        Assert.Equal(1, transport.Stops);
        relay.Dispose();
    }

    [Fact]
    public async Task SynchronouslyBlockingCleanup_IsBoundedByLifecycleTimeout()
    {
        var timeProvider = new FakeTimeProvider();
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        var transport = new LifecycleTransport(stop: _ =>
        {
            cleanupStarted.TrySetResult();
            releaseCleanup.Wait();
            return Task.CompletedTask;
        });
        var lifecycle = new OutboxTransportLifecycle(
            transport,
            TimeSpan.FromMinutes(1),
            timeProvider);
        var inner = new InMemoryOutboxStore();
        var store = new PollSignalingOutboxStore(inner);
        var relay = new OutboxRelay(
            store,
            lifecycle.CreateLease(),
            pollingInterval: TimeSpan.FromMinutes(1));

        try
        {
            await relay.StartAsync(TestContext.Current.CancellationToken);
            await store.WhenFirstPolled.WaitAsync(TestContext.Current.CancellationToken);
            var stopTask = relay.StopAsync(TestContext.Current.CancellationToken);
            await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            timeProvider.Advance(TimeSpan.FromMinutes(1));
            await stopTask;

            var exception = await Assert.ThrowsAsync<TimeoutException>(
                () => relay.ExecuteTask!);
            Assert.Contains("00:01:00", exception.Message);
            Assert.Equal(1, transport.Stops);
        }
        finally
        {
            releaseCleanup.Set();
            relay.Dispose();
        }
    }

    #endregion
}
