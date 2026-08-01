using Alberto.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Messaging;

/// <summary>
/// Tests that the retention sweep removes delivered entries on its own schedule, and that a
/// failing sweep does not take the host down with it.
/// </summary>
public class OutboxRetentionServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records the cutoffs it is swept with. Everything else on <see cref="IOutboxStore"/> throws:
    /// the retention service has no business touching the delivery path.
    /// </summary>
    private sealed class RecordingOutboxStore(Func<int, Exception?>? failOn = null) : IOutboxStore
    {
        private readonly List<DateTimeOffset> _cutoffs = [];
        private readonly SemaphoreSlim _swept = new(0);
        private readonly Lock _gate = new();

        public IReadOnlyList<DateTimeOffset> Cutoffs
        {
            get { lock (_gate) return _cutoffs.ToArray(); }
        }

        /// <summary>Waits for one more sweep than has already been observed, or gives up.</summary>
        public Task<bool> WaitForSweepAsync() => _swept.WaitAsync(Patience);

        public Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
        {
            int attempt;
            lock (_gate)
            {
                _cutoffs.Add(before);
                attempt = _cutoffs.Count;
            }

            var failure = failOn?.Invoke(attempt);
            _swept.Release();
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must not write entries.");

        public Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
            int limit, TimeSpan claimLease, string claimedBy, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must not claim entries.");

        public Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must not deliver entries.");

        public Task<bool> MarkFailedAsync(OutboxClaim claim, string error, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must not fail entries.");

        public Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must not retry entries.");
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Bounded so a loop that never exits fails the test instead of hanging the run.</summary>
    private static Task StopAsync(OutboxRetentionService service) =>
        service.StopAsync(new CancellationTokenSource(Patience).Token);

    /// <summary>
    /// Starts the service and waits until its sweep timer is armed. <c>StartAsync</c> returning is
    /// not enough — <see cref="BackgroundService"/> runs <c>ExecuteAsync</c> on the thread pool, so
    /// advancing the clock straight after it would advance past a timer that does not exist yet.
    /// </summary>
    private static async Task StartAsync(OutboxRetentionService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.Armed.WaitAsync(Patience);
    }

    [Fact]
    public async Task Sweeps_On_Interval_With_The_Retention_Cutoff()
    {
        var store = new RecordingOutboxStore();
        var time = new FakeTimeProvider(Now);
        var service = new OutboxRetentionService(
            store, retention: TimeSpan.FromDays(3), sweepInterval: TimeSpan.FromHours(1), timeProvider: time);

        await StartAsync(service);
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.WaitForSweepAsync(), "the sweep did not run");
        await StopAsync(service);

        var cutoff = Assert.Single(store.Cutoffs);
        Assert.Equal(Now.AddHours(1) - TimeSpan.FromDays(3), cutoff);
    }

    /// <summary>
    /// Host startup is the busiest moment and the moment the backlog is largest. The first sweep
    /// waits out an interval rather than piling onto it.
    /// </summary>
    [Fact]
    public async Task Does_Not_Sweep_Before_The_First_Interval_Elapses()
    {
        var store = new RecordingOutboxStore();
        var time = new FakeTimeProvider(Now);
        var service = new OutboxRetentionService(
            store, sweepInterval: TimeSpan.FromHours(1), timeProvider: time);

        await StartAsync(service);
        time.Advance(TimeSpan.FromMinutes(59));
        await StopAsync(service);

        Assert.Empty(store.Cutoffs);
    }

    [Fact]
    public async Task Infinite_Retention_Never_Sweeps()
    {
        var store = new RecordingOutboxStore();
        var time = new FakeTimeProvider(Now);
        var service = new OutboxRetentionService(
            store, retention: Timeout.InfiniteTimeSpan, sweepInterval: TimeSpan.FromHours(1), timeProvider: time);

        Assert.False(service.IsEnabled);

        // Never arms a timer, so there is nothing to wait for — it returns before it starts.
        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromDays(30));
        await StopAsync(service);

        Assert.Empty(store.Cutoffs);
    }

    /// <summary>
    /// The rows a failed sweep would have removed are inert, so the sweep retries rather than
    /// faulting the host. Verified by the second sweep happening at all.
    /// </summary>
    [Fact]
    public async Task Failed_Sweep_Does_Not_Stop_The_Service()
    {
        var store = new RecordingOutboxStore(
            failOn: attempt => attempt == 1 ? new InvalidOperationException("connection reset") : null);
        var time = new FakeTimeProvider(Now);
        var service = new OutboxRetentionService(
            store, sweepInterval: TimeSpan.FromHours(1), timeProvider: time);

        await StartAsync(service);

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.WaitForSweepAsync(), "the first sweep did not run");

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.WaitForSweepAsync(), "the service stopped after a failed sweep");

        await StopAsync(service);

        Assert.Null(service.ExecuteTask?.Exception);
    }

    [Fact]
    public void Rejects_Negative_Retention_And_Non_Positive_Interval()
    {
        var store = new RecordingOutboxStore();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OutboxRetentionService(store, retention: TimeSpan.FromDays(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OutboxRetentionService(store, sweepInterval: TimeSpan.Zero));
    }

    [Fact]
    public void Defaults_To_A_Seven_Day_Window()
    {
        Assert.Equal(TimeSpan.FromDays(7), OutboxRetentionService.DefaultRetention);
        Assert.True(new OutboxRetentionService(new RecordingOutboxStore()).IsEnabled);
    }
}
