using Microsoft.Extensions.Logging;

namespace Alberto.DeadLetters;

/// <summary>
/// Optional capability of an <see cref="IClaimableDeadLetterStore"/>: tells which of a module's
/// processors have a dead letter that <see cref="IClaimableDeadLetterStore.ClaimDueAsync"/> would
/// claim right now, in one read. Internal; a store without it keeps one claim per processor per
/// interval.
/// </summary>
internal interface IDeadLetterDueProbe
{
    /// <summary>
    /// Returns the ids among <paramref name="processorIds"/> with an entry that is flagged for
    /// retry or due at <paramref name="now"/>, and not under a live claim. Must match the claim's
    /// own predicate and clocks exactly, or a due entry is never claimed.
    /// </summary>
    Task<IReadOnlySet<string>> FindProcessorsWithDueAsync(
        IReadOnlyCollection<string> processorIds, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Replaces the idle poll of each <see cref="DeadLetterRetryLoop"/> of a module with one
/// <see cref="IDeadLetterDueProbe"/> read per interval, waking only the loops that have work.
/// The first probe runs at start. If a probe fails every loop is woken, which is exactly the
/// per-processor polling it replaces.
/// </summary>
internal sealed class DeadLetterDueGate(
    IDeadLetterDueProbe probe,
    IEnumerable<string> processorIds,
    TimeSpan interval,
    TimeProvider timeProvider,
    ILogger? logger = null) : IAsyncDisposable
{
    private readonly Dictionary<string, WakeSignal> _signals =
        processorIds.Distinct().ToDictionary(id => id, _ => new WakeSignal());
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Waits until a probe finds a due entry for <paramref name="processorId"/>.</summary>
    public Task WaitAsync(string processorId, CancellationToken cancellationToken) =>
        _signals[processorId].WaitAsync(cancellationToken);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_loop is not null)
            await _loop;
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> ids = _signals.Keys;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyCollection<string> due;
            try
            {
                due = await probe.FindProcessorsWithDueAsync(ids, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Dead-letter due probe failed; every retry loop will poll this interval.");
                due = ids;
            }

            foreach (var id in due)
                if (_signals.TryGetValue(id, out var signal))
                    signal.Notify();

            try
            {
                await Task.Delay(interval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
