using System.Collections.Concurrent;
using System.Reflection;
using Alberto.InMemory;
using Alberto.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Unit tests for <see cref="LeaseAwareControlLoopGroup"/> — the multi-tenant failover
/// component that decides whether two hosts can process the same tenant at once.
///
/// Three mutant-cluster areas are covered:
///
/// 1. <b>Lease acquisition (L69/L75)</b> — a loop is started and its fence token is
///    recorded ONLY when <c>TryAcquireAsync</c> returns a non-null lease. A null result
///    must leave the loop unstarted and the fence token at its safe-zero sentinel.
///
/// 2. <b>Early-exit guards (L160/L211/L212)</b> — <c>OnRenewalTimer</c> and
///    <c>OnScanTimer</c> must be no-ops when <c>_cts</c> is null (post-dispose / never
///    started) or when <c>_cts.IsCancellationRequested</c> (post-stop). Additionally,
///    <c>OnScanTimer</c> must skip the scan body when the <c>_scanLock</c> is already
///    held by a concurrent scan.
///
/// 3. <b>Renewal outcomes (L172/L173/L181)</b> — after <c>OnRenewalTimer</c> calls
///    <c>RenewLeasesAsync</c>, loops whose processor IDs appear in the returned set must
///    be left running; those that do not appear must have their loops stopped and their
///    fence tokens cleared. The negate mutant at L172 inverts the set-membership check,
///    so a test that asserts both directions in one go is what kills it.
///
/// <b>Deterministic timer-callback invocation:</b> <c>OnRenewalTimer</c> and
/// <c>OnScanTimer</c> are private <c>async void</c> callbacks. They are invoked via
/// reflection. Because the spy lease manager returns already-completed <c>Task</c>s
/// (<c>Task.FromResult</c>), and because <c>ControlLoop.StopAsync</c> on a never-started
/// loop returns immediately (the private <c>_loop</c> field is null, so the method exits
/// at its L138 early-return guard before reaching any <c>await</c>), every awaited path
/// inside the callbacks resolves synchronously. This makes the callbacks run to
/// completion before <c>MethodInfo.Invoke</c> returns, eliminating any race between the
/// callback body and the post-invoke assertions.
///
/// <b>Timer non-interference:</b> The spy lease manager uses <c>LeaseDuration = 1 hour</c>.
/// <c>StartAsync</c> schedules renewal at <c>LeaseDuration / 3</c> (20 min) and scan at
/// <c>LeaseDuration / 2</c> (30 min), so the real <see cref="Timer"/> objects created by
/// <c>StartAsync</c> never tick during the test. All timer-callback invocations below are
/// explicit and deterministic.
/// </summary>
public sealed class LeaseAwareControlLoopGroupTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Fake lease manager
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configurable spy lease manager. All methods return already-completed Tasks so that
    /// async-void timer callbacks execute synchronously when invoked via reflection.
    /// </summary>
    private sealed class SpyLeaseManager : IProcessorLeaseManager
    {
        // 1-hour duration → renewal timer fires at 20 min, scan timer at 30 min → neither
        // will tick during any test.
        public TimeSpan LeaseDuration => TimeSpan.FromHours(1);

        private readonly Dictionary<string, IProcessorLease?> _leases = new();
        private readonly List<string> _renewedIds = [];

        private int _tryAcquireCallCount;
        private int _renewCallCount;

        public int TryAcquireCallCount => Volatile.Read(ref _tryAcquireCallCount);
        public int RenewCallCount => Volatile.Read(ref _renewCallCount);

        public void SetLeaseFor(string processorId, IProcessorLease? lease) =>
            _leases[processorId] = lease;

        public void SetRenewedIds(params string[] ids)
        {
            _renewedIds.Clear();
            _renewedIds.AddRange(ids);
        }

        /// <summary>
        /// Reset call counters after StartAsync has run so tests can measure only the
        /// behaviour of the timer callbacks in isolation.
        /// </summary>
        public void ResetCounters()
        {
            Interlocked.Exchange(ref _tryAcquireCallCount, 0);
            Interlocked.Exchange(ref _renewCallCount, 0);
        }

        public Task<IProcessorLease?> TryAcquireAsync(
            string consumerId, string processorId, string replicaId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _tryAcquireCallCount);
            _leases.TryGetValue(processorId, out var lease);
            return Task.FromResult(lease);
        }

        public Task<IReadOnlyList<string>> RenewLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _renewCallCount);
            return Task.FromResult<IReadOnlyList<string>>(_renewedIds.ToArray());
        }

        public Task ReleaseAllLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
            string consumerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProcessorLeaseInfo>>(Array.Empty<ProcessorLeaseInfo>());
    }

    private static ProcessorLease MakeLease(string processorId, long fenceToken) =>
        new(processorId, DateTimeOffset.UtcNow.AddHours(1), fenceToken);

    // ─────────────────────────────────────────────────────────────────────────
    // Reflection helpers
    // ─────────────────────────────────────────────────────────────────────────

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static ConcurrentDictionary<string, ControlLoop> GetRunningLoops(
        LeaseAwareControlLoopGroup g) =>
        (ConcurrentDictionary<string, ControlLoop>)
            typeof(LeaseAwareControlLoopGroup).GetField("_runningLoops", Private)!.GetValue(g)!;

    private static ConcurrentDictionary<string, long> GetFenceTokens(
        LeaseAwareControlLoopGroup g) =>
        (ConcurrentDictionary<string, long>)
            typeof(LeaseAwareControlLoopGroup).GetField("_fenceTokens", Private)!.GetValue(g)!;

    private static SemaphoreSlim GetScanLock(LeaseAwareControlLoopGroup g) =>
        (SemaphoreSlim)typeof(LeaseAwareControlLoopGroup)
            .GetField("_scanLock", Private)!.GetValue(g)!;

    /// <summary>
    /// Invokes the private <c>async void OnRenewalTimer</c> callback synchronously.
    /// Because all awaited tasks inside the method complete synchronously (see class
    /// summary), the entire callback body runs before this method returns.
    /// </summary>
    private static void InvokeRenewalTimer(LeaseAwareControlLoopGroup g) =>
        typeof(LeaseAwareControlLoopGroup)
            .GetMethod("OnRenewalTimer", Private)!
            .Invoke(g, [null]);

    /// <summary>Same as <see cref="InvokeRenewalTimer"/> but for <c>OnScanTimer</c>.</summary>
    private static void InvokeScanTimer(LeaseAwareControlLoopGroup g) =>
        typeof(LeaseAwareControlLoopGroup)
            .GetMethod("OnScanTimer", Private)!
            .Invoke(g, [null]);

    /// <summary>
    /// Returns the private <c>_loop</c> Task field of a <see cref="ControlLoop"/>.
    /// This field is non-null only after <c>ControlLoop.StartAsync</c> has been called,
    /// which lets us distinguish "StartAsync was called" from merely "state was set".
    /// </summary>
    private static Task? GetControlLoopTask(ControlLoop loop) =>
        (Task?)typeof(ControlLoop).GetField("_loop", Private)!.GetValue(loop);

    // ─────────────────────────────────────────────────────────────────────────
    // Factory helpers
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class StubProcessor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>();

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Builds a minimal <see cref="ControlLoop"/> that can be constructed but never needs
    /// to be started. Used as a value in the private dictionaries that track running state.
    ///
    /// <c>StopAsync</c> on a never-started loop is safe: the internal <c>_loop</c> task
    /// is null, so <c>StopAsync</c> exits at its early-return guard before reaching any
    /// asynchronous operation. This makes it synchronous from the caller's perspective.
    /// </summary>
    private static ControlLoop BuildLoop(string processorId,
        TimeSpan? drainTimeout = null)
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(50));
        var checkpoints = new InMemoryCheckpointStore();
        var processor = new StubProcessor(processorId);
        // BatchingMode.Disabled is required because StubProcessor does not implement
        // IBatchableProcessor.
        var opts = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled };
        return new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(50), batchSize: 1,
            executionOptions: opts,
            drainTimeout: drainTimeout);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Area 1: Lease acquisition
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WhenLeaseIsNull_DoesNotStartLoopOrRecordFenceToken()
    {
        // Acquisition returning null means another replica owns the processor. The group
        // must NOT start the loop and must NOT record a fence token — zero is the safe
        // sentinel that causes every fenced checkpoint write to be rejected.
        const string procId = "proc-null-lease";
        var mgr = new SpyLeaseManager();
        mgr.SetLeaseFor(procId, null);

        var loop = BuildLoop(procId);
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [loop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None);

        group.GetFenceToken(procId).Should().Be(0,
            "a null lease means another replica owns the processor; no fence token must be stored");

        GetRunningLoops(group).Should().NotContainKey(procId,
            "the loop must not be tracked as running when lease acquisition returned null");

        // The ControlLoop's internal _loop task must remain null — StartAsync was not called.
        GetControlLoopTask(loop).Should().BeNull(
            "ControlLoop.StartAsync must not be called when the lease was not acquired");
    }

    [Fact]
    public async Task StartAsync_WhenLeaseIsAcquired_StartsLoopAndRecordsFenceToken()
    {
        // When TryAcquireAsync returns a lease, the group must:
        //   (a) record the lease's FenceToken (L74), and
        //   (b) actually call loop.StartAsync (L75) — not just update _runningLoops (L76).
        //
        // The L75 statement-deletion mutant deletes the await loop.StartAsync(...) call while
        // leaving L74 and L76 intact, so assertions on _runningLoops and GetFenceToken alone
        // would not kill it. Checking ControlLoop._loop (non-null only after StartAsync) is
        // what distinguishes the mutant from the real code.
        const string procId = "proc-good-lease";
        const long expectedToken = 99L;
        var mgr = new SpyLeaseManager();
        mgr.SetLeaseFor(procId, MakeLease(procId, expectedToken));

        // Use a short drain timeout so the started loop stops quickly during cleanup.
        var loop = BuildLoop(procId, drainTimeout: TimeSpan.FromMilliseconds(200));
        var group = new LeaseAwareControlLoopGroup(
            allLoops: [loop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        try
        {
            await group.StartAsync(CancellationToken.None);

            group.GetFenceToken(procId).Should().Be(expectedToken,
                "the fence token from the acquired lease must be stored for fenced writes");

            GetRunningLoops(group).Should().ContainKey(procId,
                "a loop with a successfully acquired lease must appear in the running set");

            // Non-null _loop confirms ControlLoop.StartAsync was actually invoked.
            GetControlLoopTask(loop).Should().NotBeNull(
                "ControlLoop.StartAsync must have been called; its internal _loop is null " +
                "on a never-started loop even if _runningLoops was populated");
        }
        finally
        {
            await group.DisposeAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Area 2: Early-exit guards in OnRenewalTimer and OnScanTimer
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnRenewalTimer_WhenCtsIsNull_IsNoOp()
    {
        // _cts is null when the group has never been started (StartAsync was never called)
        // OR when DisposeAsync has run. The guard at L160 must prevent any renewal work.
        var mgr = new SpyLeaseManager();
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");
        // StartAsync deliberately not called → _cts remains null.

        InvokeRenewalTimer(group);

        mgr.RenewCallCount.Should().Be(0,
            "RenewLeasesAsync must not be called when _cts is null (group never started)");
    }

    [Fact]
    public async Task OnRenewalTimer_WhenCancelled_IsNoOp()
    {
        // After StopAsync, _cts is non-null but IsCancellationRequested is true. The guard
        // at L160 (`_cts is null || _cts.IsCancellationRequested`) must still short-circuit.
        // Note: StopAsync cancels _cts but does not null it — that only happens in DisposeAsync.
        var mgr = new SpyLeaseManager();
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None);
        await group.StopAsync(CancellationToken.None); // cancels _cts; _cts is still non-null
        mgr.ResetCounters();                            // discard the StartAsync acquisition call

        InvokeRenewalTimer(group);

        mgr.RenewCallCount.Should().Be(0,
            "RenewLeasesAsync must not be called when _cts.IsCancellationRequested is true");
    }

    [Fact]
    public async Task OnScanTimer_WhenCtsIsNull_IsNoOp()
    {
        // Mirror of OnRenewalTimer_WhenCtsIsNull_IsNoOp for the scan path (L211 guard).
        var mgr = new SpyLeaseManager();
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        InvokeScanTimer(group);

        mgr.TryAcquireCallCount.Should().Be(0,
            "TryAcquireAsync must not be called when _cts is null (group never started)");
    }

    [Fact]
    public async Task OnScanTimer_WhenCancelled_IsNoOp()
    {
        // Mirror of OnRenewalTimer_WhenCancelled_IsNoOp for the scan path (L211 guard).
        var mgr = new SpyLeaseManager();
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None);
        await group.StopAsync(CancellationToken.None);
        mgr.ResetCounters();

        InvokeScanTimer(group);

        mgr.TryAcquireCallCount.Should().Be(0,
            "TryAcquireAsync must not be called when _cts.IsCancellationRequested is true");
    }

    [Fact]
    public async Task OnScanTimer_WhenScanAlreadyInProgress_IsSkipped()
    {
        // The L212 guard (`if (!await _scanLock.WaitAsync(0)) return`) skips the scan
        // body when a concurrent scan already holds the semaphore. Simulated by acquiring
        // _scanLock externally before invoking the callback.
        const string procId = "proc-scan-lock";
        var mgr = new SpyLeaseManager();
        mgr.SetLeaseFor(procId, null); // would be attempted if the lock weren't held

        var loop = BuildLoop(procId);
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [loop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        // StartAsync is required to set _cts so the L211 guard passes. The loop is not
        // started (TryAcquireAsync returns null).
        await group.StartAsync(CancellationToken.None);
        mgr.ResetCounters();

        // Hold _scanLock to simulate an in-progress scan.
        var scanLock = GetScanLock(group);
        await scanLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            InvokeScanTimer(group);

            // The callback's WaitAsync(0) returned false → it returned early without
            // entering the scan body → TryAcquireAsync was never called.
            mgr.TryAcquireCallCount.Should().Be(0,
                "TryAcquireAsync must not be called when _scanLock is already held by a concurrent scan");
        }
        finally
        {
            scanLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Area 3: Renewal outcomes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnRenewalTimer_ProcessorNotInRenewedSet_StopsAndClearsState()
    {
        // When RenewLeasesAsync does NOT return a processorId, the lease was lost (stolen
        // or expired). The group must stop the loop and clear both the _runningLoops entry
        // and the fence token so that fenced checkpoint writes are subsequently rejected.
        //
        // Setup: use a null-returning acquisition (so StartAsync does not start the loop),
        // then inject the loop into _runningLoops directly to simulate it was already running.
        // This is safe because ControlLoop.StopAsync on a never-started loop returns
        // immediately without any I/O.
        const string procId = "proc-not-renewed";
        var mgr = new SpyLeaseManager();
        mgr.SetLeaseFor(procId, null);  // StartAsync will not start the loop
        mgr.SetRenewedIds(/* empty — nothing renewed */);

        var loop = BuildLoop(procId);
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [loop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None); // _cts set; loop not started

        // Inject: pretend the loop is already running with a fence token of 7.
        GetRunningLoops(group)[procId] = loop;
        GetFenceTokens(group)[procId] = 7L;

        InvokeRenewalTimer(group);

        GetRunningLoops(group).Should().NotContainKey(procId,
            "a loop whose lease was not renewed must be removed from the running set");
        GetFenceTokens(group).Should().NotContainKey(procId,
            "the fence token entry must be removed when a lease is lost");
        group.GetFenceToken(procId).Should().Be(0L,
            "GetFenceToken must return the safe-zero sentinel after the lease is lost");
    }

    [Fact]
    public async Task OnRenewalTimer_ProcessorInRenewedSet_KeepsLoopRunning()
    {
        // When RenewLeasesAsync returns the processorId, the lease was successfully renewed.
        // The group must NOT stop the loop and must preserve the fence token.
        const string procId = "proc-renewed";
        const long token = 55L;
        var mgr = new SpyLeaseManager();
        mgr.SetLeaseFor(procId, null);
        mgr.SetRenewedIds(procId); // this processor's lease is renewed

        var loop = BuildLoop(procId);
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [loop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None);
        GetRunningLoops(group)[procId] = loop;
        GetFenceTokens(group)[procId] = token;

        InvokeRenewalTimer(group);

        GetRunningLoops(group).Should().ContainKey(procId,
            "a loop whose lease was successfully renewed must remain in the running set");
        group.GetFenceToken(procId).Should().Be(token,
            "the fence token must be preserved when the lease is successfully renewed");
    }

    [Fact]
    public async Task OnRenewalTimer_TwoProcessors_OnlyNonRenewedOneIsStopped()
    {
        // The critical two-direction test: kills the negate mutant at L172 that inverts
        // `renewedSet.Contains(processorId)` and stops the WRONG loops.
        //
        // One processor appears in the renewed set → must stay running.
        // One processor does NOT appear  → must be stopped and have its state cleared.
        //
        // A single-direction assertion would survive the mutant in the direction it doesn't
        // check; only asserting both directions kills it.
        const string renewedProc = "proc-renewed";
        const string lostProc = "proc-lost";
        var mgr = new SpyLeaseManager();
        mgr.SetRenewedIds(renewedProc);  // only renewedProc comes back from renewal
        mgr.SetLeaseFor(renewedProc, null);
        mgr.SetLeaseFor(lostProc, null);

        var renewedLoop = BuildLoop(renewedProc);
        var lostLoop = BuildLoop(lostProc);
        await using var group = new LeaseAwareControlLoopGroup(
            allLoops: [renewedLoop, lostLoop],
            leaseManager: mgr,
            consumerId: "consumer",
            replicaId: "replica");

        await group.StartAsync(CancellationToken.None);

        // Inject both loops as "currently running" with distinct fence tokens.
        GetRunningLoops(group)[renewedProc] = renewedLoop;
        GetRunningLoops(group)[lostProc] = lostLoop;
        GetFenceTokens(group)[renewedProc] = 1L;
        GetFenceTokens(group)[lostProc] = 2L;

        InvokeRenewalTimer(group);

        // The renewed processor must be unaffected — continue at L173 skips the cleanup.
        GetRunningLoops(group).Should().ContainKey(renewedProc,
            "the renewed processor must remain in the running set");
        group.GetFenceToken(renewedProc).Should().Be(1L,
            "the renewed processor's fence token must be preserved");

        // The non-renewed processor must be fully cleaned up — stopped at L181,
        // fence token cleared at L198, loop entry removed at L199.
        GetRunningLoops(group).Should().NotContainKey(lostProc,
            "the non-renewed processor must be removed from the running set");
        group.GetFenceToken(lostProc).Should().Be(0L,
            "the non-renewed processor's fence token must be cleared to the safe-zero sentinel");
    }
}
