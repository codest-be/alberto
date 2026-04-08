using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

public sealed class EventStoreHead : IHostedService
{
    private readonly IEventStoreBackend _backend;
    private readonly TimeSpan _refreshInterval;
    private readonly int _windowSize;
    private readonly ILogger<EventStoreHead>? _logger;
    private long _current;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    internal EventStoreHead(IEventStoreBackend backend,
        TimeSpan? refreshInterval = null, int windowSize = 2000,
        ILogger<EventStoreHead>? logger = null)
    {
        _backend = backend;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMilliseconds(100);
        _windowSize = windowSize;
        _logger = logger;
    }

    public long Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken); // warm up before agents start
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, ct);
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger?.LogWarning(ex, "EventStoreHead refresh failed"); }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var positions = await _backend.GetPositionsAsync(_current, _windowSize, ct);
        Volatile.Write(ref _current, FindContiguousHead(_current, positions));
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
