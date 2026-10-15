using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alberto.Messaging;

/// <summary>
/// Background service that polls the outbox store for pending entries and delivers them
/// via the configured <see cref="IMessageTransport"/>. A poll that fails (the store is unreachable,
/// out of connections) is logged and retried with exponential backoff; it never stops the host.
/// </summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly OutboxTransportLease _transport;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly TimeSpan _claimLease;
    private readonly string _claimedBy;
    private readonly ILogger _logger;

    private static readonly TimeSpan MinFailureBackoff = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default time for one relay to own a claimed entry before another relay may recover it.
    /// Configure a longer lease when the transport's worst-case publish time can exceed this.
    /// </summary>
    public static readonly TimeSpan DefaultClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a new <see cref="OutboxRelay"/>.
    /// </summary>
    /// <param name="store">The outbox store to poll.</param>
    /// <param name="transport">
    /// The transport to deliver messages through. This relay exclusively owns its start/stop
    /// lifecycle but does not dispose the caller-owned instance.
    /// </param>
    /// <param name="pollingInterval">How often to poll when the outbox is empty. Defaults to 1 second.</param>
    /// <param name="batchSize">Maximum number of entries to process per poll cycle. Defaults to 100.</param>
    /// <param name="claimLease">
    /// How long a claimed entry remains owned by this relay before another relay can recover it.
    /// Defaults to 5 minutes.
    /// </param>
    /// <param name="claimedBy">Diagnostic relay identity persisted with each claim.</param>
    /// <param name="logger">Receives one line per failed poll.</param>
    public OutboxRelay(
        IOutboxStore store,
        IMessageTransport transport,
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        TimeSpan? claimLease = null,
        string? claimedBy = null,
        ILogger<OutboxRelay>? logger = null)
        : this(
            store,
            CreateExclusiveTransportLease(store, transport),
            pollingInterval,
            batchSize,
            claimLease,
            claimedBy,
            logger)
    {
    }

    internal OutboxRelay(
        IOutboxStore store,
        OutboxTransportLease transport,
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        TimeSpan? claimLease = null,
        string? claimedBy = null,
        ILogger<OutboxRelay>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var resolvedClaimLease = claimLease ?? DefaultClaimLease;
        if (resolvedClaimLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimLease), "The outbox claim lease must be positive.");

        _store = store;
        _transport = transport;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(1);
        _batchSize = batchSize;
        _claimLease = resolvedClaimLease;
        _claimedBy = string.IsNullOrWhiteSpace(claimedBy)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
            : claimedBy;
        _logger = logger ?? NullLogger<OutboxRelay>.Instance;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Exception? executionFailure = null;
        ExceptionDispatchInfo? hostCancellation = null;
        try
        {
            await _transport.StartAsync(ct);

            var failureBackoff = MinFailureBackoff;
            while (!ct.IsCancellationRequested)
            {
                int claimed;
                try
                {
                    claimed = await PollOnceAsync(ct);
                    failureBackoff = MinFailureBackoff;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A store outage must not end the relay: the host would stop with it.
                    // Claims left behind expire after the lease and are picked up again.
                    _logger.LogWarning(ex, "Outbox poll failed. Retrying in {Backoff}.", failureBackoff);
                    await Task.Delay(failureBackoff, ct);
                    failureBackoff = TimeSpan.FromTicks(Math.Min(failureBackoff.Ticks * 2, MaxFailureBackoff.Ticks));
                    continue;
                }

                // Back off when there are no more pending entries
                if (claimed < _batchSize)
                    await Task.Delay(_pollingInterval, ct);
            }
        }
        catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
        {
            // Host cancellation is normal completion. If transport cleanup then fails, that
            // cleanup failure remains observable on ExecuteTask.
            hostCancellation = ExceptionDispatchInfo.Capture(exception);
        }
        catch (Exception ex)
        {
            executionFailure = ex;
            throw;
        }
        finally
        {
            await _transport.StopAsync(executionFailure);
        }

        // Preserve BackgroundService's established cancelled ExecuteTask on an ordinary host
        // stop. A cleanup failure thrown from finally above still takes precedence.
        hostCancellation?.Throw();
    }

    private async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var pending = await _store.ClaimPendingAsync(
            _batchSize,
            _claimLease,
            _claimedBy,
            ct);

        foreach (var claim in pending)
        {
            var entry = claim.Entry;
            try
            {
                await _transport.PublishAsync(
                    new ExternalMessage(
                        entry.MessageType,
                        entry.Version,
                        entry.Payload,
                        entry.Metadata,
                        Destination: entry.Destination,
                        RoutingHint: entry.RoutingHint),
                    ct);
                await _store.MarkDeliveredAsync(claim, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await _store.MarkFailedAsync(claim, ex.Message, ct);
            }
        }

        return pending.Count;
    }

    private static OutboxTransportLease CreateExclusiveTransportLease(
        IOutboxStore store,
        IMessageTransport transport)
    {
        // Preserve the public constructor's argument-validation order while delegating the rest
        // of initialization to the lease-based constructor.
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        return new OutboxTransportLifecycle(transport).CreateLease();
    }
}
