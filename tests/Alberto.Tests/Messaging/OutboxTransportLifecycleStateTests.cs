using System.Reflection;
using Alberto.Messaging;
using Xunit;

namespace Alberto.Tests.Messaging;

/// <summary>
/// Tests that directly exercise the state-machine branches of <see cref="OutboxTransportLifecycle"/>
/// and <see cref="OutboxTransportLease"/> that are not reached by the higher-level relay tests.
///
/// All tests run in-memory with no Docker or database.
/// </summary>
public class OutboxTransportLifecycleStateTests
{
    // -----------------------------------------------------------------------
    // Minimal tracking transport — records start/stop/publish call counts
    // and optionally injects a startup failure.
    // -----------------------------------------------------------------------
    private sealed class TrackingTransport : IMessageTransport
    {
        private readonly Exception? _startFailure;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Publishes { get; private set; }

        public TrackingTransport(Exception? startFailure = null) =>
            _startFailure = startFailure;

        public Task StartAsync(CancellationToken ct)
        {
            Starts++;
            return _startFailure is not null
                ? Task.FromException(_startFailure)
                : Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Task PublishAsync(ExternalMessage message, CancellationToken ct)
        {
            Publishes++;
            return Task.CompletedTask;
        }
    }

    private static ExternalMessage SampleMessage() =>
        new("test.event", "1", "{}", new Dictionary<string, string>());

    // -----------------------------------------------------------------------
    // L35 — constructor rejects non-positive stopTimeout
    //
    // Mutation: delete or neutralise the ArgumentOutOfRangeException throw.
    // Kill: the constructor must reject TimeSpan.Zero (and any negative span).
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsZeroStopTimeout()
    {
        var transport = new TrackingTransport();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OutboxTransportLifecycle(transport, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_RejectsNegativeStopTimeout()
    {
        var transport = new TrackingTransport();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OutboxTransportLifecycle(transport, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Constructor_AcceptsPositiveStopTimeout()
    {
        // Ensure the positive path does NOT throw — guards the boundary from both sides.
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport, TimeSpan.FromSeconds(1));
        Assert.NotNull(lifecycle);
    }

    // -----------------------------------------------------------------------
    // L59 — stored startup failure is re-thrown to subsequent callers
    //
    // When the first lease's StartAsync causes the transport to fail, the
    // lifecycle captures the exception in _startupFailure. A second lease that
    // then calls StartAsync must receive *the same exception instance*, not a
    // generic "already stopped" error.
    //
    // Mutation: delete _startupFailure.Throw().
    // Kill: deleting it falls through to the L63 guard which throws a
    // different InvalidOperationException instance, so Assert.Same fails.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_RethrowsStoredStartupFailure_ToSubsequentCaller()
    {
        var startupFailure = new ApplicationException("transport failed to initialise");
        var transport = new TrackingTransport(startFailure: startupFailure);
        var lifecycle = new OutboxTransportLifecycle(transport);

        // First lease — triggers the startup failure and causes the lifecycle
        // to record it in _startupFailure, then transition to Stopped.
        var lease1 = lifecycle.CreateLease();
        var firstException = await Assert.ThrowsAsync<ApplicationException>(
            () => lease1.StartAsync(CancellationToken.None));
        Assert.Same(startupFailure, firstException);

        // Second lease — lifecycle is Stopped and _startupFailure is set.
        // L59 rethrows the captured instance; without it the code would fall
        // through to L63 and raise a different InvalidOperationException.
        var lease2 = lifecycle.CreateLease();
        var secondException = await Assert.ThrowsAsync<ApplicationException>(
            () => lease2.StartAsync(CancellationToken.None));

        Assert.Same(startupFailure, secondException);
    }

    // -----------------------------------------------------------------------
    // L63 — restarting a cleanly-stopped lifecycle is rejected
    //
    // After a lifecycle reaches Stopped through a *normal* stop (not a
    // startup failure), _startupFailure is null. Attempting to start it again
    // must throw InvalidOperationException.
    //
    // Mutation: delete the L63 throw.
    // Kill: without the guard the lifecycle would silently attempt to start
    // again from Stopped state, not throw at all.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ThrowsAfterLifecycleHasBeenStopped()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);

        // Run one full start → stop cycle to get the lifecycle into Stopped state.
        var lease1 = lifecycle.CreateLease();
        await lease1.StartAsync(CancellationToken.None);
        await lease1.StopAsync(null);

        // A brand-new lease from the same (now-Stopped) lifecycle must be rejected.
        var lease2 = lifecycle.CreateLease();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease2.StartAsync(CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // L111 — StopAsync on an unstarted lifecycle is a no-op
    //
    // Calling StopAsync when the lifecycle is still in Created state must
    // return without touching the transport.
    //
    // Mutation: delete the early return on L111.
    // Kill: without the guard StopAsync would decrement _activeRelays, set the
    // state to Stopping/Stopped, and call StopTransportAsync — causing
    // transport.Stops > 0.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_BeforeStart_IsNoOp_TransportStopNotCalled()
    {
        var transport = new TrackingTransport();
        // Access the lifecycle directly (not through a lease) so the lease's
        // own _state guard does not mask the lifecycle's L111 guard.
        var lifecycle = new OutboxTransportLifecycle(transport);

        await lifecycle.StopAsync(null);

        Assert.Equal(0, transport.Stops);
    }

    // -----------------------------------------------------------------------
    // L212 — double-starting the same lease is rejected
    //
    // OutboxTransportLease.StartAsync uses a CAS to transition state 0 → 1.
    // A second call on the same lease sees state 1 (or higher) and must throw.
    //
    // Mutation: delete the InvalidOperationException throw on L212.
    // Kill: without the guard the second StartAsync call would call
    // _owner.StartAsync again, potentially double-starting the transport.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Lease_StartAsync_ThrowsIfCalledTwice()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);
        var lease = lifecycle.CreateLease();

        // First start succeeds.
        await lease.StartAsync(CancellationToken.None);

        // Second start on the same lease must be rejected immediately,
        // without starting the transport a second time.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.StartAsync(CancellationToken.None));

        Assert.Equal(1, transport.Starts);
    }

    // -----------------------------------------------------------------------
    // L230 — publishing before the lease is started is rejected
    //
    // OutboxTransportLease.PublishAsync checks _state == 2 (Running). Any
    // other state must throw InvalidOperationException.
    //
    // Mutations: delete the L230 throw, or flip the != 2 check.
    // Kill: assertions confirm the throw happens both before start and after stop.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Lease_PublishAsync_ThrowsBeforeStart()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);
        var lease = lifecycle.CreateLease();

        // Lease is in state 0 (Created), so _state != 2 → must throw.
        // PublishAsync is Task-returning, so use ThrowsAsync even though the
        // throw is synchronous: the async wrapper still catches it correctly.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.PublishAsync(SampleMessage(), CancellationToken.None));
    }

    [Fact]
    public async Task Lease_PublishAsync_ThrowsAfterStop()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);
        var lease = lifecycle.CreateLease();

        await lease.StartAsync(CancellationToken.None);
        await lease.StopAsync(null);

        // After stop, _state is 4 — must still throw.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.PublishAsync(SampleMessage(), CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // L260 — StopCoreAsync writes state 4 (Stopped) in its finally block
    //
    // After StopAsync completes on a lease, the lease's internal _state must
    // be 4. No public API directly exposes _state, so reflection is the only
    // way to distinguish state 3 (Stopping — left behind if L260 is deleted)
    // from state 4 (Stopped — the correct terminal value).
    //
    // Mutation: delete Volatile.Write(ref _state, 4) on L260.
    // Kill: the field stays 3 instead of becoming 4, failing the assertion.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Lease_InternalState_Is4_AfterStopCompletes()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);
        var lease = lifecycle.CreateLease();

        await lease.StartAsync(CancellationToken.None);
        await lease.StopAsync(null);

        // Reflect on the private _state field. The field type is int; the
        // named states (from OutboxTransportLease's implicit state machine) are:
        //   0 = Created, 1 = Starting, 2 = Running, 3 = Stopping, 4 = Stopped/Failed
        var stateField = typeof(OutboxTransportLease)
            .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(stateField);  // Guard: ensures reflection found the field.

        var rawValue = stateField.GetValue(lease);
        Assert.NotNull(rawValue);

        var stateValue = (int)rawValue;
        Assert.Equal(4, stateValue);
    }

    // -----------------------------------------------------------------------
    // Additional: second StopAsync on a lease returns a Task without calling
    // the underlying transport again (idempotence guard).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Lease_StopAsync_IsIdempotent()
    {
        var transport = new TrackingTransport();
        var lifecycle = new OutboxTransportLifecycle(transport);
        var lease = lifecycle.CreateLease();

        await lease.StartAsync(CancellationToken.None);

        var task1 = lease.StopAsync(null);
        var task2 = lease.StopAsync(null);

        await Task.WhenAll(task1, task2);

        Assert.Equal(1, transport.Stops);
    }
}
