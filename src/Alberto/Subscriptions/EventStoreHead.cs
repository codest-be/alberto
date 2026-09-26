using Alberto.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Subscriptions;

public sealed class EventStoreHead : IHostedService
{
    private readonly IEventStoreHeadBackend _backend;
    private readonly TimeSpan _refreshInterval;
    private readonly int _windowSize;
    private readonly ILogger<EventStoreHead>? _logger;
    private readonly IEventAppendedSignal? _signal;
    private readonly TimeSpan _drainTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly string _moduleKey;
    private readonly TimeSpan _stallWarningThreshold;
    private long _current;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Set when the barrier starts holding the head back without it advancing; cleared the
    // moment it advances or the barrier releases. Backs both the "how long has this been
    // stuck" warning below and the alberto.head.stalled gauge.
    private DateTimeOffset? _stallStartedAt;

    // Warn once per stall, not on every poll: the gauge above is what an alert rule watches
    // continuously, so the log only needs to name the likely cause once per incident, not
    // spam the log every _refreshInterval until an operator intervenes.
    private bool _warnedForCurrentStall;

    internal EventStoreHead(IEventStoreHeadBackend backend,
        TimeSpan? refreshInterval = null, int windowSize = 2000,
        ILogger<EventStoreHead>? logger = null,
        IEventAppendedSignal? signal = null,
        TimeSpan? drainTimeout = null,
        TimeProvider? timeProvider = null,
        string moduleKey = "",
        TimeSpan? stallWarningThreshold = null)
    {
        _backend = backend;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(100);
        _windowSize = windowSize;
        _logger = logger;
        _signal = signal;
        _drainTimeout = drainTimeout ?? Configuration.ControlLoopOptions.Default.DrainTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _moduleKey = moduleKey;
        _stallWarningThreshold = stallWarningThreshold ?? TimeSpan.FromSeconds(30);
    }

    public long Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken); // warm up before agents start
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The warm-up is an optimisation — the loop below refreshes on its own interval and
            // already tolerates a failing backend. Rethrowing here would mean one unreachable
            // shard takes down a host whose other databases are fine, and the module's real
            // startup check on that database (migration and tenancy-mode validation) has already
            // run and recorded the failure.
            _logger?.LogWarning(ex, "EventStoreHead warm-up failed; starting cold and retrying on the poll interval");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
    }

    /// <summary>
    /// Cancels the refresh loop and waits for it to exit, bounded by the configured drain
    /// timeout so a backend call that ignores cancellation cannot stall host shutdown.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try { await _cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        if (_loop is null) return;

        try
        {
            await _loop.WaitAsync(_drainTimeout, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            _logger?.LogWarning(
                "EventStoreHead did not stop within {DrainTimeout}; abandoning the wait.",
                _drainTimeout);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WaitForNextRefreshAsync(ct);
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger?.LogWarning(ex, "EventStoreHead refresh failed"); }
        }
    }

    /// <summary>
    /// Waits for the polling interval, or wakes early when the backend pushes an
    /// append notification (LISTEN/NOTIFY). The interval remains an upper bound so
    /// the head still advances if a notification is missed or unavailable.
    /// </summary>
    private Task WaitForNextRefreshAsync(CancellationToken ct)
        => _signal is null
            ? Task.Delay(_refreshInterval, ct)
            : _signal.WaitAsync(_refreshInterval, ct);

    /// <summary>
    /// Performs one poll: advances the contiguous head, clamps it to the stable-head barrier,
    /// and updates stall tracking. Internal (rather than private) so tests can drive individual
    /// polls deterministically without spinning up the background loop.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        var current = _current;
        var positions = await _backend.GetPositionsAsync(current, _windowSize, ct);
        var head = FindContiguousHead(current, positions);

        // Clamp to the in-flight visibility barrier: never advance past an append
        // whose transaction has not committed yet. Backends without a barrier
        // (IEventStoreHeadBackend default) return long.MaxValue, leaving the
        // contiguous head unchanged.
        var stableHead = await _backend.GetStableHeadAsync(current, ct);
        var holding = stableHead < head;
        if (holding)
            head = stableHead;

        TrackStall(holding, headAdvanced: head != current);
        Volatile.Write(ref _current, head);
    }

    /// <summary>
    /// Tracks how long the barrier has held the head back without it advancing, warns once
    /// per stall past <see cref="_stallWarningThreshold"/>, logs once when it clears, and
    /// keeps the <c>alberto.head.stalled</c> gauge in sync either way.
    /// </summary>
    private void TrackStall(bool holding, bool headAdvanced)
    {
        if (holding && !headAdvanced)
        {
            _stallStartedAt ??= _timeProvider.GetUtcNow();
            var elapsed = _timeProvider.GetUtcNow() - _stallStartedAt.Value;

            if (elapsed >= _stallWarningThreshold && !_warnedForCurrentStall)
            {
                _warnedForCurrentStall = true;
                _logger?.LogWarning(
                    "EventStoreHead for module '{ModuleKey}' has been stalled for {Elapsed} — the " +
                    "stable-head barrier is holding the head below a committed event, most likely " +
                    "because a write transaction elsewhere on the Postgres server (this database or " +
                    "another one on the same instance) is still open and pinning the xmin horizon. " +
                    "Find the blocker with 'SELECT pid, xact_start, state, query FROM pg_stat_activity " +
                    "WHERE backend_xid IS NOT NULL ORDER BY age(backend_xid) DESC' (a read-only " +
                    "session has backend_xmin but no backend_xid and is not the cause) and end it; if " +
                    "nothing turns up, also check 'SELECT gid, prepared, owner, database FROM " +
                    "pg_prepared_xacts' for an orphaned prepared transaction. Setting " +
                    "idle_in_transaction_session_timeout prevents an abandoned session from doing this " +
                    "again, though it does not cover a long-running active transaction or an orphaned " +
                    "prepared one.",
                    _moduleKey, elapsed);
            }

            // Mirrors the warning: the gauge reads 0 until the hold has actually crossed the
            // threshold, so a routine few-second hold (a normal short-lived write transaction)
            // never shows up as "stalled" on a dashboard, only a genuine stall does.
            AlbertoMetrics.RecordHeadStalled(
                _moduleKey, elapsed >= _stallWarningThreshold ? elapsed.TotalSeconds : 0d);
        }
        else
        {
            if (_warnedForCurrentStall)
            {
                _logger?.LogInformation(
                    "EventStoreHead for module '{ModuleKey}' is no longer stalled; the stable-head " +
                    "barrier has released.",
                    _moduleKey);
            }

            _stallStartedAt = null;
            _warnedForCurrentStall = false;
            AlbertoMetrics.RecordHeadStalled(_moduleKey, 0d);
        }
    }

    /// <summary>
    /// Advances from afterPosition through positions, skipping over permanent gaps
    /// (deleted events) while still stopping at the tail where a gap might indicate
    /// an in-flight transaction that hasn't committed yet.
    ///
    /// A gap is considered permanent if there are more committed events after it
    /// within the window. A gap at the very end of the window is treated as a
    /// potential concurrency boundary — we stop before it.
    ///
    /// (5, [6,7,8,10]) → 8   — gap at tail, could be in-flight
    /// (5, [6,7,8,10,11]) → 11 — gap in middle, permanent, skip over
    /// (5, [6,7,8,9]) → 9     — no gaps
    /// (5, []) → 5             — no events
    /// (5, [7,8]) → 8          — gap at start is permanent (events follow)
    /// (5, [7]) → 5            — gap at start, nothing follows, could be in-flight
    /// </summary>
    internal static long FindContiguousHead(long afterPosition, IReadOnlyList<long> positions)
    {
        if (positions.Count == 0)
            return afterPosition;

        // A gap at the tail might be an uncommitted transaction, so we cannot
        // advance past the last position that is followed by another position.
        // Walk backwards from the end to find the safe head: the last position
        // whose *next* position also exists in the window, OR the very last
        // position if the sequence ends gap-free.
        //
        // Strategy: advance through all positions. For each gap encountered,
        // only skip it if there is at least one more position after it (proving
        // the gap is permanent). Stop at the last position before a tail gap.

        var head = afterPosition;
        for (var i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos != head + 1)
            {
                // Gap detected between head and pos.
                // If this is the first position (gap from afterPosition) or a mid-stream gap,
                // it's only safe to skip if there are more positions after this one.
                if (i + 1 < positions.Count)
                {
                    // More positions follow — this gap is permanent, skip over it.
                    head = pos;
                }
                else
                {
                    // This is the last position and there's a gap before it.
                    // Could be an in-flight transaction — stop before it.
                    break;
                }
            }
            else
            {
                head = pos;
            }
        }

        return head;
    }
}
