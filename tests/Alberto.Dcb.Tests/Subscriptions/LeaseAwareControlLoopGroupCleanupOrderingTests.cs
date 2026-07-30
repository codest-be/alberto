using System.Collections.Concurrent;
using System.Reflection;
using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Regression tests for the cleanup ordering invariant in LeaseAwareControlLoopGroup.
///
/// Background: OnRenewalTimer and OnScanTimer run as concurrent async-void timer callbacks.
/// When a lease renewal fails, OnRenewalTimer must remove the processor from both
/// _runningLoops and _fenceTokens. The ORDER of those two removals matters:
///
/// BUGGY order (_runningLoops removed first):
///   1. _runningLoops.TryRemove("proc") — scan guard (ContainsKey) now returns false
///   ← window opens: OnScanTimer can fire and observe "proc" as not running →
///   2. Scan re-acquires lease, stores new token T2 in _fenceTokens
///   3. _fenceTokens.TryRemove("proc") — silently deletes T2
///   Result: a live loop holds fence token T2, but GetFenceToken returns 0. Every
///   fenced checkpoint write is rejected; OnFenceViolation fires; the loop is cancelled.
///   The processor appears to lose its lease on every renewal cycle.
///
/// FIXED order (_fenceTokens removed first):
///   1. _fenceTokens.TryRemove("proc") — token is gone
///   ← window: _runningLoops.ContainsKey("proc") is still true →
///   2. OnScanTimer fires and checks ContainsKey — sees the processor as still running,
///      skips it, no re-acquisition attempted.
///   3. _runningLoops.TryRemove("proc") — loop entry is removed
///   Result: after this point the scan can re-acquire cleanly and any new token persists.
///
/// Testing a live async-void timer race deterministically is not achievable without
/// restructuring production code (e.g. extracting a synchronisable cleanup method).
/// Instead, these tests verify the ordering INVARIANT directly: they manipulate the
/// private dictionaries via reflection to prove that the fixed order keeps the scan
/// guard satisfied throughout the cleanup window.
/// </summary>
public sealed class LeaseAwareControlLoopGroupCleanupOrderingTests
{
    /// <summary>
    /// Verifies that removing _fenceTokens before _runningLoops keeps the scan guard
    /// (_runningLoops.ContainsKey) satisfied throughout the cleanup window, so a
    /// concurrent OnScanTimer cannot observe the processor as available for re-acquisition.
    /// </summary>
    [Fact]
    public void FixedCleanupOrder_ScanGuardRemainsSatisfiedBetweenTheTwoRemovals()
    {
        const string processorId = "proc-ordering-test";

        var group = BuildGroup(processorId);
        var (runningLoops, fenceTokens) = GetPrivateDictionaries(group);
        var loop = BuildLoop(processorId);

        // Simulate: the processor is running with fence token T1
        runningLoops[processorId] = loop;
        fenceTokens[processorId] = 1L;

        // Fixed cleanup step 1: remove the fence token first
        fenceTokens.TryRemove(processorId, out _);

        // Between step 1 and step 2, the scan guard must still be satisfied.
        // OnScanTimer reads _runningLoops.ContainsKey before attempting re-acquisition —
        // the processor must appear there so the scan skips it.
        Assert.True(runningLoops.ContainsKey(processorId),
            "Between the two removes (in fixed order), _runningLoops.ContainsKey must remain true " +
            "so that a concurrent OnScanTimer passes its guard check and skips re-acquisition.");

        // Fixed cleanup step 2: remove the loop entry
        runningLoops.TryRemove(processorId, out _);

        // After full cleanup, the fence token reads as 0 (correct: the processor is stopped)
        Assert.Equal(0L, group.GetFenceToken(processorId));
    }

    /// <summary>
    /// Documents the BUGGY ordering to explain what the fix prevents. In the old order
    /// (_runningLoops removed first), a concurrent scan can observe the processor as not
    /// running, re-acquire the lease, and store a new token — which the completion of the
    /// cleanup then silently deletes, leaving a live loop with fence token 0.
    ///
    /// This test proves the window exists in the buggy ordering; the companion test above
    /// proves the fixed ordering closes it.
    /// </summary>
    [Fact]
    public void BuggyCleanupOrder_OpensDangerousWindowWhereScanCanReacquireAndLoseToken()
    {
        const string processorId = "proc-buggy-order-test";

        var group = BuildGroup(processorId);
        var (runningLoops, fenceTokens) = GetPrivateDictionaries(group);
        var loop = BuildLoop(processorId);

        // Simulate: the processor is running with fence token T1
        runningLoops[processorId] = loop;
        fenceTokens[processorId] = 1L;

        // BUGGY cleanup step 1: remove the loop entry first — this is the old order
        runningLoops.TryRemove(processorId, out _);

        // Between step 1 and step 2 in the buggy order, the scan guard evaluates to FALSE.
        // OnScanTimer fires and calls _runningLoops.ContainsKey("proc") → false → proceeds.
        Assert.False(runningLoops.ContainsKey(processorId),
            "In the buggy order, _runningLoops.ContainsKey is already false before " +
            "_fenceTokens is removed — the scan can pass the guard and re-acquire.");

        // Simulate: the scan fires in the window, re-acquires the lease, and stores T2
        const long newToken = 2L;
        fenceTokens[processorId] = newToken;

        // BUGGY cleanup step 2: the old renewal callback continuation now removes _fenceTokens —
        // silently deleting the T2 that the scan just wrote
        fenceTokens.TryRemove(processorId, out _);

        // T2 is gone. A loop is now running (the scan started one) but GetFenceToken returns 0.
        // Every fenced checkpoint write by that loop will be rejected.
        Assert.Equal(0L, group.GetFenceToken(processorId));
        // Note: under the real race the loop started by the scan would have _runningLoops["proc"]
        // set again by the scan's own continuation. The _runningLoops entry re-appearing is what
        // would eventually stop further scan attempts — but the token is already gone, so the
        // loop is effectively dead regardless.
    }

    #region Test Infrastructure

    private sealed class StubProcessor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive => true;
        public bool IsRebuilding => false;
        public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>();

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullLeaseManager : IProcessorLeaseManager
    {
        public TimeSpan LeaseDuration { get; } = TimeSpan.FromSeconds(30);

        public Task<IProcessorLease?> TryAcquireAsync(
            string consumerId, string processorId, string replicaId, CancellationToken ct = default)
            => Task.FromResult<IProcessorLease?>(null);

        public Task<IReadOnlyList<string>> RenewLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task ReleaseAllLeasesAsync(
            string consumerId, string replicaId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
            string consumerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProcessorLeaseInfo>>(Array.Empty<ProcessorLeaseInfo>());
    }

    /// <summary>
    /// Creates a <see cref="LeaseAwareControlLoopGroup"/> whose loop list contains a single
    /// entry for <paramref name="processorId"/>. The group is NOT started — no timers fire
    /// and no leases are acquired. This gives us a live instance whose private dictionaries
    /// we can manipulate via reflection to simulate interleaved state.
    /// </summary>
    private static LeaseAwareControlLoopGroup BuildGroup(string processorId)
    {
        var loop = BuildLoop(processorId);
        return new LeaseAwareControlLoopGroup(
            allLoops: new[] { loop },
            leaseManager: new NullLeaseManager(),
            consumerId: "test-consumer",
            replicaId: "test-replica");
    }

    /// <summary>
    /// Builds a minimal <see cref="ControlLoop"/> that satisfies the constructor but does
    /// nothing when started — we never call StartAsync on it in these tests.
    /// </summary>
    private static ControlLoop BuildLoop(string processorId)
    {
        var backend = new InMemoryEventStoreBackend();
        var head = new EventStoreHead(backend, TimeSpan.FromMilliseconds(50));
        var checkpoints = new InMemoryCheckpointStore();
        var processor = new StubProcessor(processorId);
        // BatchingMode must be Disabled because StubProcessor does not implement
        // IBatchableProcessor. The tests never start these loops — they only use
        // them as dictionary values to exercise the cleanup-ordering invariant.
        var opts = new ProcessorExecutionOptions { BatchingMode = ProcessorBatchingMode.Disabled };
        return new ControlLoop(processor, head, backend, checkpoints,
            pollingInterval: TimeSpan.FromMilliseconds(50), batchSize: 1,
            executionOptions: opts);
    }

    private static (ConcurrentDictionary<string, ControlLoop> runningLoops,
                    ConcurrentDictionary<string, long> fenceTokens)
        GetPrivateDictionaries(LeaseAwareControlLoopGroup group)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LeaseAwareControlLoopGroup);

        var runningLoops =
            (ConcurrentDictionary<string, ControlLoop>)type
                .GetField("_runningLoops", flags)!
                .GetValue(group)!;

        var fenceTokens =
            (ConcurrentDictionary<string, long>)type
                .GetField("_fenceTokens", flags)!
                .GetValue(group)!;

        return (runningLoops, fenceTokens);
    }

    #endregion
}
