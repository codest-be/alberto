using System.Collections.Concurrent;
using System.Diagnostics;
using Alberto.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Alberto.Leases;

namespace Alberto.Subscriptions;

/// <summary>
/// Lease-aware wrapper for multiple ControlLoops. Acquires a per-processor lease
/// before starting each loop, renews leases periodically, and releases them on
/// graceful shutdown for fast handoff during blue/green deployments.
/// </summary>
internal sealed class LeaseAwareControlLoopGroup : IHostedService, IAsyncDisposable, ILiveLoopSuspender
{
    private readonly IReadOnlyList<ControlLoop> _allLoops;
    private readonly IProcessorLeaseManager _leaseManager;
    private readonly string _consumerId;
    private readonly string _replicaId;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task>? _flushCheckpoints;
    private readonly Func<string, CancellationToken, Task>? _forgetCheckpoint;

    /// <summary>Set when <see cref="Configuration.ProcessorLeaseOptions.Rebalance"/> is on.</summary>
    private readonly IRebalancingLeaseManager? _rebalancer;

    /// <summary>
    /// This replica's presence lease, so a replica that holds no processor still counts as live.
    /// The colon keeps it apart from processor ids, which are module-chosen names.
    /// </summary>
    private string PresenceId => $"alberto:replica:{_replicaId}";
    private readonly ConcurrentDictionary<string, ControlLoop> _runningLoops = new();

    /// <summary>
    /// The fence token of the lease this replica currently holds per processor. Fenced
    /// checkpoint writes present it, so it has to track acquisitions exactly: set when a lease
    /// is acquired, cleared when one is lost or handed back.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _fenceTokens = new();
    private readonly ConcurrentDictionary<string, ControlLoop> _loopsByProcessorId;
    private Timer? _renewalTimer;
    private Timer? _scanTimer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>
    /// Until when (<see cref="Environment.TickCount64"/>) a replica below its fair share scans at
    /// the short interval. Set when the share grows, so a replica that stays below it for good
    /// (6 processors over 4 replicas leaves one with 1 of 2) calms down after one lease duration.
    /// </summary>
    private long _hungryUntil;
    private int _lastShare;

    public LeaseAwareControlLoopGroup(
        IReadOnlyList<ControlLoop> allLoops,
        IProcessorLeaseManager leaseManager,
        string consumerId,
        string replicaId,
        ILogger? logger = null,
        Func<CancellationToken, Task>? flushCheckpoints = null,
        bool rebalance = false,
        Func<string, CancellationToken, Task>? forgetCheckpoint = null)
    {
        if (rebalance)
        {
            // Never fall back to renewing every row: that revives the leases being shed.
            _rebalancer = leaseManager as IRebalancingLeaseManager
                ?? throw new InvalidOperationException(
                    $"Lease rebalancing needs a lease manager that can renew a subset of leases and count live replicas; " +
                    $"{leaseManager.GetType().Name} cannot. Turn ProcessorLeaseOptions.Rebalance off or use the Postgres or in-memory backend.");
        }

        _flushCheckpoints = flushCheckpoints;
        _forgetCheckpoint = forgetCheckpoint;
        _allLoops = allLoops;
        _leaseManager = leaseManager;
        _consumerId = consumerId;
        _replicaId = replicaId;
        _logger = logger;
        _loopsByProcessorId = new ConcurrentDictionary<string, ControlLoop>(
            allLoops.ToDictionary(l => l.ProcessorId, l => l));
    }

    /// <summary>
    /// The fence token of the lease held for <paramref name="processorId"/>, or 0 when this
    /// replica holds none. 0 is below every token the database issues, so a write presenting it
    /// is refused — which is the right answer for a replica that does not own the processor.
    /// </summary>
    /// <summary>
    /// Stops one running loop for a rebuild promotion, holding the scan lock until the returned
    /// handle is disposed.
    /// </summary>
    /// <remarks>
    /// The lock is the point. The periodic scan starts any leased processor it does not find in
    /// <see cref="_runningLoops"/>, so without it a scan landing mid-promotion would restart the
    /// very writer the promotion stopped. The loop's entry stays in <see cref="_runningLoops"/>
    /// throughout — the scan skips what is listed there, and this replica does still own the
    /// processor; it is only parked.
    /// </remarks>
    public async ValueTask<IAsyncDisposable> SuspendAsync(string processorId, CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);

        try
        {
            // Not running here means another replica holds the lease, so there is no local
            // writer to stop. The lock is still held: releasing it would let this replica pick
            // the processor up mid-promotion, which is the same race by a longer route.
            if (!_runningLoops.TryGetValue(processorId, out var loop))
                return new Suspension(this, loop: null, processorId);

            await loop.StopAsync(cancellationToken);
            return new Suspension(this, loop, processorId);
        }
        catch
        {
            _scanLock.Release();
            throw;
        }
    }

    private sealed class Suspension(
        LeaseAwareControlLoopGroup group, ControlLoop? loop, string processorId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Only restart a loop this replica still owns. A lease lost during the promotion
                // takes the entry out of _runningLoops, and restarting on the strength of a
                // handle captured before that would put a fenced-out replica back to work.
                if (loop is not null && group._runningLoops.ContainsKey(processorId))
                    await loop.StartAsync(CancellationToken.None);
            }
            finally
            {
                group._scanLock.Release();
            }
        }
    }

    /// <summary>
    /// Stops one processor's loop after its fenced checkpoint write was rejected and forgets
    /// the lease bookkeeping, so the next scan can re-acquire it if the lease is available.
    /// The other processors keep running.
    /// </summary>
    public async Task StopProcessorAsync(string processorId)
    {
        if (!_runningLoops.TryGetValue(processorId, out var loop)) return;

        try { await loop.StopAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Error stopping loop for processor {ProcessorId} after fence violation", processorId);
        }

        // Token before loop entry, for the same reason as in OnRenewalTimer.
        _fenceTokens.TryRemove(processorId, out _);
        _runningLoops.TryRemove(processorId, out _);
    }

    /// <summary>The loop for <paramref name="processorId"/> when this replica holds its lease, else null.</summary>
    internal ControlLoop? HeldLoop(string processorId) =>
        _runningLoops.TryGetValue(processorId, out var loop) ? loop : null;

    public long GetFenceToken(string processorId) =>
        _fenceTokens.TryGetValue(processorId, out var token) ? token : 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Captured by both timers and by every loop started below, so their logs carry it too.
        using var scope = _logger?.BeginScope(new Dictionary<string, object?>
        {
            ["alberto.consumer.id"] = _consumerId,
            ["alberto.replica.id"] = _replicaId,
        });
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var share = await FairShareAsync(cancellationToken);
        NoteShare(share);

        // Try to acquire leases for all processors and start those we get
        if (BatchAcquirer is { } batch)
            await AcquireAndStartManyAsync(batch, _allLoops, cancellationToken);
        else foreach (var loop in _allLoops)
        {
            if (_runningLoops.Count >= share) break;

            try
            {
                var lease = await AcquireAsync(loop.ProcessorId, cancellationToken);

                if (lease is not null)
                {
                    _logger?.LogInformation(
                        "Acquired lease for processor {ProcessorId} (expires {ExpiresAt})",
                        loop.ProcessorId, lease.ExpiresAt);
                    _fenceTokens[loop.ProcessorId] = lease.FenceToken;
                    await loop.StartAsync(cancellationToken);
                    _runningLoops[loop.ProcessorId] = loop;
                }
                else
                {
                    _logger?.LogDebug(
                        "Could not acquire lease for processor {ProcessorId} — held by another replica",
                        loop.ProcessorId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to acquire lease for processor {ProcessorId}",
                    loop.ProcessorId);
            }
        }

        _logger?.LogInformation(
            "Lease-aware control loop started: {Acquired}/{Total} processors acquired by replica {ReplicaId}",
            _runningLoops.Count, _allLoops.Count, _replicaId);

        // Start renewal timer (every leaseDuration / 3)
        var renewalInterval = _leaseManager.LeaseDuration / 3;
        _renewalTimer = new Timer(OnRenewalTimer, null, renewalInterval, renewalInterval);

        // Start the scan timer to pick up released processors
        var scanInterval = NextScanInterval(share);
        _scanTimer = new Timer(OnScanTimer, null, scanInterval, scanInterval);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop timers first
        if (_renewalTimer is not null) await _renewalTimer.DisposeAsync();
        if (_scanTimer is not null) await _scanTimer.DisposeAsync();
        _renewalTimer = null;
        _scanTimer = null;

        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        // A scan already past its cancellation checks would otherwise acquire a lease and start
        // a loop after the cleanup below. Bounded, because a rebuild suspension holds the same
        // lock and shutdown must not wait on it indefinitely; past the bound, clean up anyway.
        var scanLocked = false;
        try { scanLocked = await _scanLock.WaitAsync(_leaseManager.LeaseDuration, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { } // second dispose

        try
        {
            // Stop all running loops in parallel
            var stopTasks = _runningLoops.Values
                .Select(l => l.StopAsync(cancellationToken))
                .ToArray();
            await Task.WhenAll(stopTasks);

            // Flush buffered checkpoints while the leases and fence tokens are still ours: a fenced
            // write presents the token, so after the clear and release below it would be refused
            // and the final positions lost.
            if (_flushCheckpoints is not null)
            {
                try { await _flushCheckpoints(cancellationToken); }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to flush checkpoints on shutdown for replica {ReplicaId}", _replicaId);
                }
            }

            _runningLoops.Clear();
            _fenceTokens.Clear();

            // Release all leases immediately for fast handoff
            try
            {
                await ReleaseAllAsync(cancellationToken);
                _logger?.LogInformation(
                    "Released all processor leases for replica {ReplicaId}",
                    _replicaId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to release processor leases for replica {ReplicaId} — they will expire naturally",
                    _replicaId);
            }
        }
        finally
        {
            if (scanLocked) _scanLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
            await Task.WhenAll(_allLoops.Select(l => l.DisposeAsync().AsTask()));
            _scanLock.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _cts, null)?.Dispose();
        }
    }

    private async void OnRenewalTimer(object? state) => await RenewOnceAsync();

    /// <summary>One renewal pass; the timer's body, awaitable for tests.</summary>
    internal async Task RenewOnceAsync()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        // The timer captured StartAsync's context: its log scope, which is wanted, and the span
        // the host started it under, which is not. See BackgroundSpans.
        Activity.Current = null;

        try
        {
            var (renewed, shed) = await RenewAsync(_cts.Token);

            var renewedSet = new HashSet<string>(renewed, StringComparer.Ordinal);
            var stopped = new List<string>();
            var release = new List<string>();

            // Stop any running loops whose leases were not renewed (stolen/expired, or shed)
            foreach (var (processorId, loop) in _runningLoops)
            {
                if (renewedSet.Contains(processorId))
                    continue;

                if (shed.Contains(processorId))
                    _logger?.LogInformation(
                        "Shedding processor {ProcessorId} to stay within this replica's fair share", processorId);
                else
                    _logger?.LogWarning(
                        "Lease for processor {ProcessorId} was not renewed — stopping loop",
                        processorId);

                try
                {
                    await loop.StopAsync(CancellationToken.None);
                    if (shed.Contains(processorId)) release.Add(processorId);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "Error stopping loop for processor {ProcessorId} after lease loss",
                        processorId);
                }

                stopped.Add(processorId);
            }

            // A shed lease is still ours, so its last checkpoint passes the fence, but only while
            // the fence token is still on record. Flush first.
            if (shed.Count > 0 && _flushCheckpoints is not null)
            {
                try { await _flushCheckpoints(CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to flush checkpoints for shed processors");
                    release.Clear(); // their positions are not written yet: let the leases expire instead
                }
            }

            // Hand the stopped and flushed ones back now, so another replica's scan takes them
            // within seconds instead of after expiry. Before the entries go: while a processor is
            // still in _runningLoops our own scan cannot re-take it, so the rows deleted are ours.
            if (release.Count > 0)
            {
                try { await _rebalancer!.ReleaseLeasesAsync(_consumerId, _replicaId, release, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to release shed leases — they will expire naturally");
                }
            }

            foreach (var processorId in stopped)
            {
                // Remove the fence token BEFORE removing the loop entry. OnScanTimer's only guard
                // is _runningLoops.ContainsKey; if the loop entry were removed first, a concurrent
                // scan could pass that guard, re-acquire the lease, and store a new token T2 — then
                // this continuation would remove T2, leaving the newly-started loop with fence
                // token 0 and causing every fenced checkpoint write to be rejected. Keeping the
                // loop entry present for the full duration of the window blocks the scan from
                // attempting re-acquisition until the cleanup is complete. Removing an absent key
                // from a ConcurrentDictionary is a harmless no-op, so the inverted order is safe.
                _fenceTokens.TryRemove(processorId, out _);
                _runningLoops.TryRemove(processorId, out _);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during lease renewal");
        }
    }

    private async void OnScanTimer(object? state) => await ScanOnceAsync();

    /// <summary>One scan pass; the timer's body, awaitable for tests.</summary>
    internal async Task ScanOnceAsync()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        if (!await _scanLock.WaitAsync(0)) return; // Skip if a scan is already in progress
        Activity.Current = null; // as in OnRenewalTimer

        try
        {
            var share = await FairShareAsync(_cts.Token);
            NoteShare(share);

            if (BatchAcquirer is { } batch)
                await AcquireAndStartManyAsync(
                    batch, _allLoops.Where(l => !_runningLoops.ContainsKey(l.ProcessorId)).ToList(), _cts.Token);
            else foreach (var loop in _allLoops)
            {
                if (_cts.IsCancellationRequested) break;
                // Checked on every acquire: TryAcquireAsync hands a replica back its own
                // unexpired row, so without the cap a scan would re-take what renewal just shed.
                if (_runningLoops.Count >= share) break;
                if (_runningLoops.ContainsKey(loop.ProcessorId)) continue;

                try
                {
                    var lease = await AcquireAsync(loop.ProcessorId, _cts.Token);

                    if (lease is null) continue;

                    _logger?.LogInformation(
                        "Acquired lease for processor {ProcessorId} via scan (expires {ExpiresAt})",
                        loop.ProcessorId, lease.ExpiresAt);

                    _fenceTokens[loop.ProcessorId] = lease.FenceToken;
                    await loop.StartAsync(_cts.Token);
                    _runningLoops[loop.ProcessorId] = loop;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to acquire/start processor {ProcessorId} during scan",
                        loop.ProcessorId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during processor scan");
        }
        finally
        {
            try
            {
                var next = NextScanInterval(_lastShare);
                _scanTimer?.Change(next, next);
            }
            catch (ObjectDisposedException) { /* stopping */ }

            _scanLock.Release();
        }
    }

    /// <summary>A grown share opens a window of fast scans to pick up what others release.</summary>
    private void NoteShare(int share)
    {
        if (_rebalancer is not null && share > _lastShare)
            _hungryUntil = Environment.TickCount64 + (long)_leaseManager.LeaseDuration.TotalMilliseconds;
        _lastShare = share;
    }

    /// <summary>
    /// LeaseDuration / 12 while below the share inside the window, else LeaseDuration / 2. A
    /// replica at its share never scans faster than before.
    /// </summary>
    private TimeSpan NextScanInterval(int share) =>
        _runningLoops.Count < share && Environment.TickCount64 < _hungryUntil
            ? _leaseManager.LeaseDuration / 12
            : _leaseManager.LeaseDuration / 2;

    private Activity? StartLeaseSpan(string name)
    {
        var activity = BackgroundSpans.StartRoot(name);
        activity?.SetTag("alberto.consumer.id", _consumerId);
        activity?.SetTag("alberto.replica.id", _replicaId);
        return activity;
    }

    private async Task<IProcessorLease?> AcquireAsync(string processorId, CancellationToken cancellationToken)
    {
        using var activity = StartLeaseSpan("alberto.lease.acquire");
        activity?.SetTag("alberto.processor.id", processorId);
        try
        {
            var lease = await _leaseManager.TryAcquireAsync(_consumerId, processorId, _replicaId, cancellationToken);
            activity?.SetTag("alberto.lease.acquired", lease is not null);
            // The loop starts right after this; it must read where the last owner left off.
            if (lease is not null && _forgetCheckpoint is not null)
                await _forgetCheckpoint(processorId, cancellationToken);
            if (lease is not null)
                activity?.SetTag("alberto.lease.fence_token", lease.FenceToken);
            return lease;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            BackgroundSpans.Fail(activity, ex);
            throw;
        }
    }

    // One round trip for every candidate, but only while the share is unbounded (rebalancing
    // off): with a capped share, how many to take depends on how many are won, one by one.
    private IBatchProcessorLeaseManager? BatchAcquirer =>
        _rebalancer is null ? _leaseManager as IBatchProcessorLeaseManager : null;

    private async Task AcquireAndStartManyAsync(
        IBatchProcessorLeaseManager batch, IReadOnlyList<ControlLoop> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return;

        IReadOnlyList<IProcessorLease> leases;
        using (var activity = StartLeaseSpan("alberto.lease.acquire_many"))
        {
            activity?.SetTag("alberto.lease.requested", candidates.Count);
            try
            {
                leases = await batch.TryAcquireManyAsync(
                    _consumerId, candidates.Select(l => l.ProcessorId).ToArray(), _replicaId, cancellationToken);
                activity?.SetTag("alberto.lease.acquired_count", leases.Count);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    BackgroundSpans.Fail(activity, ex);
                _logger?.LogWarning(ex, "Failed to acquire leases for {Count} processors", candidates.Count);
                return;
            }
        }

        foreach (var lease in leases)
        {
            if (cancellationToken.IsCancellationRequested) break; // the unstarted leases expire
            if (!_loopsByProcessorId.TryGetValue(lease.ProcessorId, out var loop)) continue;
            try
            {
                // Same order as the single path: forget, token, start, then running.
                if (_forgetCheckpoint is not null)
                    await _forgetCheckpoint(lease.ProcessorId, cancellationToken);
                _logger?.LogInformation(
                    "Acquired lease for processor {ProcessorId} (expires {ExpiresAt})",
                    lease.ProcessorId, lease.ExpiresAt);
                _fenceTokens[lease.ProcessorId] = lease.FenceToken;
                await loop.StartAsync(cancellationToken);
                _runningLoops[lease.ProcessorId] = loop;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start processor {ProcessorId}", lease.ProcessorId);
            }
        }
    }

    /// <summary>
    /// ceil(processors / live replicas), after refreshing this replica's presence lease.
    /// Unlimited when rebalancing is off.
    /// </summary>
    private async Task<int> FairShareAsync(CancellationToken cancellationToken)
    {
        if (_rebalancer is null) return int.MaxValue;

        try
        {
            await _leaseManager.TryAcquireAsync(_consumerId, PresenceId, _replicaId, cancellationToken);
            var live = Math.Max(1, await _rebalancer.CountLiveReplicasAsync(_consumerId, cancellationToken));
            return (_allLoops.Count + live - 1) / live;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Unknown share: keep what is held, take nothing new.
            _logger?.LogWarning(ex, "Failed to compute the fair share for replica {ReplicaId}", _replicaId);
            return _runningLoops.Count;
        }
    }

    private async Task<(IReadOnlyList<string> Renewed, IReadOnlySet<string> Shed)> RenewAsync(
        CancellationToken cancellationToken)
    {
        using var activity = StartLeaseSpan("alberto.lease.renew");
        try
        {
            IReadOnlyList<string> renewed;
            IReadOnlySet<string> shed = new HashSet<string>();
            if (_rebalancer is null)
            {
                renewed = await _leaseManager.RenewLeasesAsync(_consumerId, _replicaId, cancellationToken);
            }
            else
            {
                // Keep the first processors in declaration order, so every renewal sheds the same ones.
                var share = await FairShareAsync(cancellationToken);
                var keep = _allLoops.Select(l => l.ProcessorId).Where(_runningLoops.ContainsKey).Take(share).ToList();
                shed = _runningLoops.Keys.Except(keep).ToHashSet(StringComparer.Ordinal);
                renewed = await _rebalancer.RenewLeasesAsync(
                    _consumerId, _replicaId, [.. keep, PresenceId], cancellationToken);
                activity?.SetTag("alberto.lease.shed", shed.Count);
            }

            activity?.SetTag("alberto.lease.renewed", renewed.Count);
            activity?.SetTag("alberto.lease.lost", _runningLoops.Keys.Count(id => !renewed.Contains(id)));
            return (renewed, shed);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            BackgroundSpans.Fail(activity, ex);
            throw;
        }
    }

    private async Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        using var activity = StartLeaseSpan("alberto.lease.release");
        try
        {
            await _leaseManager.ReleaseAllLeasesAsync(_consumerId, _replicaId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            BackgroundSpans.Fail(activity, ex);
            throw;
        }
    }
}
