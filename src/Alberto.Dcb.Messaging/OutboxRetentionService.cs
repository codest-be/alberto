using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Background service that periodically deletes outbox entries delivered longer ago than the
/// configured retention window.
/// </summary>
/// <remarks>
/// <para>
/// A delivered entry has done its job: the message reached the transport, and the row that
/// survives it is a record, not a work item. Without something to remove them the outbox is
/// append-only in practice — it grows for the life of the system, and the indexes the relay
/// depends on grow with it.
/// </para>
/// <para>
/// This is deliberately a separate service rather than a step on the relay's loop. The relay's
/// job is latency: a purge that takes seconds — and the first one after this feature ships can,
/// because it faces the whole accumulated backlog at once — would be seconds in which nothing is
/// published. Here it runs on its own schedule and a slow sweep delays only the next sweep.
/// </para>
/// <para>
/// Sweeps are idempotent, so several replicas running this concurrently is safe: they compete to
/// delete the same rows and the losers delete nothing. It is wasteful rather than wrong.
/// </para>
/// </remarks>
public sealed class OutboxRetentionService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the sweep timer exists and the loop is waiting on it.
    /// </summary>
    /// <remarks>
    /// <see cref="BackgroundService"/> starts <see cref="ExecuteAsync"/> on the thread pool, so
    /// <see cref="BackgroundService.StartAsync"/> returning says nothing about the loop having
    /// begun. A test that advances a fake clock at that point can advance past a timer that does
    /// not exist yet and lose the tick. Nothing outside the tests needs this — real clocks do not
    /// jump — which is why it is internal.
    /// </remarks>
    internal Task Armed => _armed.Task;

    /// <summary>
    /// How long a delivered entry is kept when no retention is configured explicitly.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the sweep runs when no interval is configured explicitly.
    /// </summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Creates a new <see cref="OutboxRetentionService"/>.
    /// </summary>
    /// <param name="store">The outbox store to sweep.</param>
    /// <param name="retention">
    /// How long to keep an entry after delivery. Defaults to <see cref="DefaultRetention"/>.
    /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to keep delivered entries forever, which
    /// disables sweeping entirely.
    /// </param>
    /// <param name="sweepInterval">
    /// How often to sweep. Defaults to <see cref="DefaultSweepInterval"/>. This bounds how far
    /// past the retention window an entry can survive, not how long it is kept.
    /// </param>
    /// <param name="timeProvider">Clock used to age entries and to schedule sweeps.</param>
    /// <param name="logger">Receives one line per sweep failure. Sweeps that succeed are silent.</param>
    public OutboxRetentionService(
        IOutboxStore store,
        TimeSpan? retention = null,
        TimeSpan? sweepInterval = null,
        TimeProvider? timeProvider = null,
        ILogger<OutboxRetentionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var resolvedRetention = retention ?? DefaultRetention;
        if (resolvedRetention != Timeout.InfiniteTimeSpan && resolvedRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "Outbox retention must be non-negative, or Timeout.InfiniteTimeSpan to keep delivered entries forever.");

        var resolvedInterval = sweepInterval ?? DefaultSweepInterval;
        if (resolvedInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), "The outbox sweep interval must be positive.");

        _store = store;
        _retention = resolvedRetention;
        _interval = resolvedInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OutboxRetentionService>.Instance;
    }

    /// <summary>
    /// Whether this service will delete anything. <see langword="false"/> when retention is
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public bool IsEnabled => _retention != Timeout.InfiniteTimeSpan;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!IsEnabled)
            return;

        // Wait before the first sweep rather than after it. Starting a host is when it is busiest
        // and when the purge has the most to do, and nothing is urgent about rows that have
        // already outlived the window by an unbounded margin.
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        _armed.TrySetResult();

        while (await SafeWaitAsync(timer, ct))
        {
            try
            {
                await _store.PurgeDeliveredAsync(_timeProvider.GetUtcNow() - _retention, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A failed sweep is not worth taking the host down for: the rows it would have
                // removed are inert, and the next sweep faces the same work plus a little more.
                _logger.LogWarning(ex, "Outbox retention sweep failed. Retrying in {Interval}.", _interval);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
