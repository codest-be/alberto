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
    /// Advances from afterPosition through a gap-free prefix. Stops at first gap.
    /// Pure static — easy to unit test.
    /// (5, [6,7,8,10]) → 8 | (5, [6,7,8,9]) → 9 | (5, []) → 5 | (5, [7,8]) → 5
    /// </summary>
    internal static long FindContiguousHead(long afterPosition, IReadOnlyList<long> positions)
    {
        var head = afterPosition;
        foreach (var pos in positions)
        {
            if (pos != head + 1) break;
            head = pos;
        }
        return head;
    }
}
