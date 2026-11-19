using System.Collections.Concurrent;
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
    private readonly AlbertoInstruments _instruments;
    private long _current;

    // Completed and swapped each time the head advances, so caught-up control loops wake on the
    // advance instead of on their polling interval. Many waiters, one broadcast.
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The last position of every event type the head has passed, cumulative from position 0,
    // written before the head that covers it is published. Off for good once the backend cannot
    // say which types a window held (a third-party backend without IHeadSnapshotBackend).
    private readonly ConcurrentDictionary<string, long> _lastOfType = new(StringComparer.Ordinal);
    private volatile bool _typesTracked = true;

    // Whether the last refresh found the stable-head barrier holding the head back. A hold ends
    // when a transaction ends, which sends no NOTIFY, so it keeps the short interval.
    private bool _holding;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Set when the barrier starts holding the head back without it advancing; cleared the
    // moment it advances or the barrier releases. Backs both the "how long has this been
    // stuck" warning below and the alberto.head.stalled gauge.
    private DateTimeOffset? _stallStartedAt;

    // Warn once per incident (one continuous hold, until the barrier releases), not on every
    // poll: the gauge is what an alert rule watches continuously, so the log only needs to
    // name the likely cause once.
    private bool _warnedForCurrentStall;

    internal EventStoreHead(IEventStoreHeadBackend backend,
        TimeSpan? refreshInterval = null, int windowSize = 2000,
        ILogger<EventStoreHead>? logger = null,
        IEventAppendedSignal? signal = null,
        TimeSpan? drainTimeout = null,
        TimeProvider? timeProvider = null,
        string moduleKey = "",
        TimeSpan? stallWarningThreshold = null,
        AlbertoInstruments? instruments = null)
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
        _instruments = instruments ?? AlbertoMetrics.Shared;
    }

    public long Current => Volatile.Read(ref _current);

    /// <summary>
    /// Completes the next time <see cref="Current"/> advances. Take it <em>before</em> reading
    /// <see cref="Current"/>: an advance after that read then always completes the task in hand.
    /// </summary>
    internal Task Advanced => Volatile.Read(ref _advanced).Task;

    /// <summary>
    /// True when the head knows that no event of <paramref name="types"/> lies after
    /// <paramref name="afterPosition"/> and at or below the <see cref="Current"/> the caller read
    /// before calling this, so a processor at <paramref name="afterPosition"/> can move to that head
    /// without reading. Every position at or below the head was in a window the head read, and the
    /// window's types are recorded before the head is published, so the record covers it. Positions
    /// past the head may be recorded too; that only makes the answer "no" more often. An empty set
    /// answers no: the read decides what it means.
    /// </summary>
    internal bool HasNoneOf(IReadOnlySet<string> types, long afterPosition)
    {
        if (!_typesTracked || types.Count == 0)
            return false;
        foreach (var type in types)
            if (_lastOfType.TryGetValue(type, out var last) && last > afterPosition)
                return false;
        return true;
    }

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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WaitForNextRefreshAsync(cancellationToken);
                    await RefreshAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger?.LogWarning(ex, "EventStoreHead refresh failed"); }
            }
        }
        finally
        {
            // A stopped head is not a stalled one, nor a healthy one: it has no reading at all.
            _instruments.ForgetHeadStalled(_moduleKey);
        }
    }

    /// <summary>
    /// Waits for the polling interval, or wakes early when the backend pushes an
    /// append notification (LISTEN/NOTIFY). The interval remains an upper bound so
    /// the head still advances if a notification is missed or unavailable.
    /// </summary>
    private Task WaitForNextRefreshAsync(CancellationToken cancellationToken)
    {
        // A gap still unproven usually proves on the next snapshot (a rollback sends no NOTIFY), so
        // look again soon. The proof waits on every write transaction on the server, not just this
        // store's, so the backoff stays at tens of milliseconds rather than growing to the interval.
        var wait = NextRefreshWait;
        return _signal is null
            ? Task.Delay(wait, cancellationToken)
            : _signal.WaitAsync(wait, cancellationToken);
    }

    /// <summary>
    /// How long to wait before the next refresh. A pending gap proof retries soon. Otherwise, while
    /// LISTEN is up every committed append wakes the head, so the interval is only a safety net and
    /// backs off to <see cref="IdleWaitWhileListening"/>; without it (no listener, a dropped
    /// connection, or a barrier hold that ends with no NOTIFY) the configured interval is the bound.
    /// </summary>
    internal TimeSpan NextRefreshWait => NextWait(_refreshInterval, _gapRetry,
        listening: _signal is EventAppendedSignal { IsListening: true } && !_holding);

    internal static TimeSpan NextWait(TimeSpan refreshInterval, TimeSpan gapRetry, bool listening) =>
        gapRetry > TimeSpan.Zero && gapRetry < refreshInterval ? gapRetry
        : listening && refreshInterval < IdleWaitWhileListening ? IdleWaitWhileListening
        : refreshInterval;

    internal static readonly TimeSpan IdleWaitWhileListening = TimeSpan.FromSeconds(1);

    private TimeSpan _gapRetry;
    private static readonly TimeSpan FirstGapRetry = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxGapRetry = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Performs one poll: advances the contiguous head, clamps it to the stable-head barrier,
    /// and updates stall tracking. Internal (rather than private) so tests can drive individual
    /// polls deterministically without spinning up the background loop.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var start = _current;
        var current = start;
        var holding = false;
        var gapHeld = false;

        // A window holds at most _windowSize positions, so catching up on a large log takes many
        // of them: keep going while each one comes back full instead of one window per poll.
        // Bounded so a log that keeps growing under us cannot pin this refresh forever.
        for (var window = 0; window < MaxWindowsPerRefresh; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await HeadSnapshot.ReadAsync(_backend, current, _windowSize, cancellationToken);
            var read = snapshot.Window;
            var positions = read.Positions;
            var head = read.Horizon is { } horizon
                ? AdvanceThroughProvenGaps(current, positions, horizon, out gapHeld)
                : FindContiguousHead(current, positions);

            // Clamp to the in-flight visibility barrier: never advance past an append
            // whose transaction has not committed yet. Backends without a barrier
            // (IEventStoreHeadBackend default) return long.MaxValue, leaving the
            // contiguous head unchanged.
            // Only the window just read can be clamped, so the backend is asked about that range
            // and nothing past it; and an empty or gapped-at-the-start window has nothing to clamp.
            // A snapshot backend computed it over the whole window in the same statement; a first
            // unstable event past the head gives a stable head at or above it, which clamps nothing.
            var stableHead = head > current
                ? snapshot.StableHead ?? await _backend.GetStableHeadUpToAsync(current, head, cancellationToken)
                : long.MaxValue;
            holding = gapHeld || stableHead < head;
            if (stableHead < head)
                head = stableHead;

            var advanced = head > current;
            current = head;
            RecordTypes(snapshot.LastOfType);
            Volatile.Write(ref _current, current);

            if (holding || !advanced || positions.Count < _windowSize)
                break;
        }

        _gapRetry = !gapHeld ? TimeSpan.Zero
            : _gapRetry == TimeSpan.Zero ? FirstGapRetry
            : _gapRetry * 2 < MaxGapRetry ? _gapRetry * 2 : MaxGapRetry;
        _holding = holding;
        TrackStall(holding, headAdvanced: current != start);
        if (current != start)
            Interlocked.Exchange(ref _advanced, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private void RecordTypes(IReadOnlyDictionary<string, long>? lastOfType)
    {
        if (lastOfType is null)
        {
            _typesTracked = false;
            return;
        }
        foreach (var (type, last) in lastOfType)
            _lastOfType.AddOrUpdate(type, last, (_, seen) => Math.Max(seen, last));
    }

    private const int MaxWindowsPerRefresh = 1000;

    // Gaps at or below _provenThrough are permanent. _unprovenGap is the last position in the log when a
    // gap was first seen and the horizon's NextToStart then: once no transaction older than that is
    // running, nothing at or below that position can still appear.
    private long _provenThrough;
    private (long Below, long Horizon)? _unprovenGap;

    /// <summary>
    /// Advances through <paramref name="positions"/> and over the gaps proven permanent. A gap is a
    /// position drawn by an append that has not committed, or by one that rolled back; positions after
    /// it do not tell which, because appends of different tenants draw from one sequence concurrently.
    /// The backend gives each append its transaction id before it draws a position, so every position at
    /// or below the last one a snapshot sees was drawn by a transaction older than that snapshot's
    /// NextToStart. Once a snapshot's OldestRunning reaches it (the same snapshot when nothing was
    /// running), those transactions have all ended, and a gap still there is permanent. The proof covers
    /// the whole log as it stood, so catching up over old gaps proves them all at once.
    /// </summary>
    private long AdvanceThroughProvenGaps(long afterPosition, IReadOnlyList<long> positions, TransactionHorizon horizon, out bool held)
    {
        held = false;
        var head = afterPosition;
        foreach (var position in positions)
        {
            if (position != head + 1 && position - 1 > _provenThrough)
            {
                // An earlier gap's proof may cover this one; if not, this snapshot starts a new proof,
                // which it completes itself when nothing was running.
                TryProve(horizon);
                if (position - 1 > _provenThrough)
                {
                    _unprovenGap ??= (horizon.LastPosition, horizon.NextToStart);
                    TryProve(horizon);
                }
                if (position - 1 > _provenThrough)
                {
                    held = true;
                    break;
                }
            }
            head = position;
        }
        return head;
    }

    private void TryProve(TransactionHorizon horizon)
    {
        if (_unprovenGap is not { } gap || horizon.OldestRunning < gap.Horizon) return;
        _provenThrough = Math.Max(_provenThrough, gap.Below);
        _unprovenGap = null;
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
            _instruments.RecordHeadStalled(
                _moduleKey, elapsed >= _stallWarningThreshold ? elapsed.TotalSeconds : 0d);
        }
        else
        {
            // Any advance restarts the clock, so a head that keeps creeping forward never reads as
            // stalled. But the incident only ends when the barrier stops holding: a partial advance
            // keeps it open, so one continuous hold warns once and logs one release.
            _stallStartedAt = null;
            if (!holding)
            {
                if (_warnedForCurrentStall)
                    _logger?.LogInformation(
                        "EventStoreHead for module '{ModuleKey}' is no longer stalled; the stable-head " +
                        "barrier has released.",
                        _moduleKey);
                _warnedForCurrentStall = false;
            }

            _instruments.RecordHeadStalled(_moduleKey, 0d);
        }
    }

    /// <summary>
    /// Advances from afterPosition through positions, skipping over permanent gaps
    /// (deleted events) while still stopping at the tail where a gap might indicate
    /// an in-flight transaction that hasn't committed yet.
    ///
    /// A gap is considered permanent if there are more committed events after it
    /// within the window. The window is the next <c>windowSize</c> committed positions, not
    /// a position range, so a gap wider than the window does not hide what follows it. A gap at the very end of the window is treated as a
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
