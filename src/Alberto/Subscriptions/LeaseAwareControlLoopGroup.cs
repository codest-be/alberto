using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Subscriptions;

/// <summary>
/// Lease-aware wrapper for multiple ControlLoops. Acquires a per-processor lease
/// before starting each loop, renews leases periodically, and releases them on
/// graceful shutdown for fast handoff during blue/green deployments.
/// </summary>
internal sealed class LeaseAwareControlLoopGroup : IHostedService, IAsyncDisposable
{
    private readonly IReadOnlyList<ControlLoop> _allLoops;
    private readonly IProcessorLeaseManager _leaseManager;
    private readonly string _consumerId;
    private readonly string _replicaId;
    private readonly ILogger? _logger;
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

    public LeaseAwareControlLoopGroup(
        IReadOnlyList<ControlLoop> allLoops,
        IProcessorLeaseManager leaseManager,
        string consumerId,
        string replicaId,
        ILogger? logger = null)
    {
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
    public long GetFenceToken(string processorId) =>
        _fenceTokens.TryGetValue(processorId, out var token) ? token : 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Try to acquire leases for all processors and start those we get
        foreach (var loop in _allLoops)
        {
            try
            {
                var lease = await _leaseManager.TryAcquireAsync(
                    _consumerId, loop.ProcessorId, _replicaId, cancellationToken);

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

        // Start scan timer (every leaseDuration / 2) to pick up released processors
        var scanInterval = _leaseManager.LeaseDuration / 2;
        _scanTimer = new Timer(OnScanTimer, null, scanInterval, scanInterval);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop timers first
        if (_renewalTimer is not null) await _renewalTimer.DisposeAsync();
        if (_scanTimer is not null) await _scanTimer.DisposeAsync();
        _renewalTimer = null;
        _scanTimer = null;

        // Stop all running loops in parallel
        var stopTasks = _runningLoops.Values
            .Select(l => l.StopAsync(cancellationToken))
            .ToArray();
        await Task.WhenAll(stopTasks);
        _runningLoops.Clear();
        _fenceTokens.Clear();

        // Release all leases immediately for fast handoff
        try
        {
            await _leaseManager.ReleaseAllLeasesAsync(_consumerId, _replicaId, cancellationToken);
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

        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
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

    private async void OnRenewalTimer(object? state)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;

        try
        {
            var renewed = await _leaseManager.RenewLeasesAsync(
                _consumerId, _replicaId, _cts.Token);

            var renewedSet = new HashSet<string>(renewed, StringComparer.Ordinal);

            // Stop any running loops whose leases were not renewed (stolen/expired)
            foreach (var (processorId, loop) in _runningLoops)
            {
                if (renewedSet.Contains(processorId))
                    continue;

                _logger?.LogWarning(
                    "Lease for processor {ProcessorId} was not renewed — stopping loop",
                    processorId);

                try
                {
                    await loop.StopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "Error stopping loop for processor {ProcessorId} after lease loss",
                        processorId);
                }

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

    private async void OnScanTimer(object? state)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        if (!await _scanLock.WaitAsync(0)) return; // Skip if a scan is already in progress

        try
        {
            foreach (var loop in _allLoops)
            {
                if (_cts.IsCancellationRequested) break;
                if (_runningLoops.ContainsKey(loop.ProcessorId)) continue;

                try
                {
                    var lease = await _leaseManager.TryAcquireAsync(
                        _consumerId, loop.ProcessorId, _replicaId, _cts.Token);

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
            _scanLock.Release();
        }
    }
}
