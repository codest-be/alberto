using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Alberto.Diagnostics;
using Alberto.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alberto.Messaging;

/// <summary>
/// Background service that polls the outbox store for pending entries and delivers them
/// via the configured <see cref="IMessageTransport"/>.
/// </summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly AlbertoInstruments _instruments;
    private readonly IOutboxStore _store;
    private readonly OutboxTransportLease _transport;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly TimeSpan _claimLease;
    private readonly string _claimedBy;
    private readonly int _maxRetries;
    private readonly ILogger _logger;

    /// <summary>
    /// Default time for one relay to own a claimed entry before another relay may recover it.
    /// Configure a longer lease when the transport's worst-case publish time can exceed this.
    /// </summary>
    public static readonly TimeSpan DefaultClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default number of failed delivery attempts after which an entry is parked as <c>failed</c>.
    /// </summary>
    public const int DefaultMaxRetries = 5;

    // Upper bound for the exponential back-off after failures, unless the polling interval is longer.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // Bounds the mark-delivered write that follows a publish, so a shutdown does not skip it
    // (the message would be delivered twice) yet cannot hang on a stuck store either.
    private static readonly TimeSpan MarkDeliveredTimeout = TimeSpan.FromSeconds(5);

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
    public OutboxRelay(
        IOutboxStore store,
        IMessageTransport transport,
        TimeSpan? pollingInterval,
        int batchSize,
        TimeSpan? claimLease,
        string? claimedBy)
        : this(store, transport, pollingInterval, batchSize, claimLease, claimedBy, DefaultMaxRetries)
    {
    }

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
    /// <param name="maxRetries">
    /// How many failed delivery attempts an entry gets before it is parked as <c>failed</c>.
    /// Defaults to <see cref="DefaultMaxRetries"/>. Between attempts the relay backs off.
    /// </param>
    /// <param name="logger">Receives claim, delivery and bookkeeping failures.</param>
    public OutboxRelay(
        IOutboxStore store,
        IMessageTransport transport,
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        TimeSpan? claimLease = null,
        string? claimedBy = null,
        int maxRetries = DefaultMaxRetries,
        ILogger<OutboxRelay>? logger = null)
        : this(
            store,
            CreateExclusiveTransportLease(store, transport),
            pollingInterval,
            batchSize,
            claimLease,
            claimedBy,
            maxRetries,
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
        int maxRetries = DefaultMaxRetries,
        ILogger<OutboxRelay>? logger = null,
        AlbertoInstruments? instruments = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _instruments = instruments ?? AlbertoMetrics.Shared;
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);

        var resolvedClaimLease = claimLease ?? DefaultClaimLease;
        if (resolvedClaimLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimLease), "The outbox claim lease must be positive.");

        _store = store;
        _transport = transport;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(1);
        _batchSize = batchSize;
        _claimLease = resolvedClaimLease;
        _maxRetries = maxRetries;
        _logger = logger ?? NullLogger<OutboxRelay>.Instance;
        _claimedBy = string.IsNullOrWhiteSpace(claimedBy)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
            : claimedBy;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A send span with no recorded parent must be a root, and StartActivity would otherwise
        // adopt whatever span the host was started under. See BackgroundSpans.
        Activity.Current = null;
        Exception? executionFailure = null;
        ExceptionDispatchInfo? hostCancellation = null;
        try
        {
            await _transport.StartAsync(ct);

            var failureStreak = 0;
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<OutboxClaim> pending;
                try
                {
                    pending = await _store.ClaimPendingAsync(
                        _batchSize,
                        _claimLease,
                        _claimedBy,
                        ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A store outage is transient from the relay's point of view: unclaimed
                    // entries stay pending, so back off and poll again instead of stopping the host.
                    failureStreak++;
                    var delay = Backoff(_pollingInterval, failureStreak);
                    _logger.LogWarning(ex, "Outbox claim failed. Retrying in {Delay}.", delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                var anyPublishFailed = false;
                foreach (var claim in pending)
                {
                    var entry = claim.Entry;
                    using var scope = entry.TenantId is null
                        ? null
                        : _logger.BeginScope(new Dictionary<string, object?> { ["alberto.tenant.id"] = entry.TenantId });
                    using var activity = StartSendActivity(entry);
                    try
                    {
                        await _transport.PublishAsync(BuildMessage(entry, activity), ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        anyPublishFailed = true;
                        var errorType = ex.GetType().FullName ?? ex.GetType().Name;
                        BackgroundSpans.Fail(activity, ex);
                        _instruments.OutboxPublishFailures.Add(1, new KeyValuePair<string, object?>("error.type", errorType));
                        await MarkFailedSafelyAsync(claim, ex, ct);
                        continue;
                    }

                    _instruments.OutboxPublished.Add(1);
                    _instruments.OutboxDeliveryLatency.Record((DateTimeOffset.UtcNow - entry.CreatedAt).TotalSeconds);
                    // Published either way: a failed mark leaves the span Ok and the metrics alone,
                    // and surfaces here so the redelivery it will cause can be explained.
                    var marked = await MarkDeliveredSafelyAsync(claim);
                    activity?.SetTag("alberto.outbox.marked_delivered", marked);
                }

                failureStreak = anyPublishFailed ? failureStreak + 1 : 0;

                if (anyPublishFailed)
                    await Task.Delay(Backoff(_pollingInterval, failureStreak), ct);
                // Back off when there are no more pending entries
                else if (pending.Count < _batchSize)
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

    internal static TimeSpan Backoff(TimeSpan pollingInterval, int failureStreak)
    {
        var cap = pollingInterval > MaxBackoff ? pollingInterval : MaxBackoff;
        var factor = Math.Pow(2, Math.Min(failureStreak - 1, 16));
        return TimeSpan.FromTicks((long)Math.Min(pollingInterval.Ticks * factor, cap.Ticks));
    }

    private async Task MarkFailedSafelyAsync(OutboxClaim claim, Exception failure, CancellationToken ct)
    {
        try
        {
            if (!await _store.MarkAttemptFailedAsync(claim, failure.Message, _maxRetries, ct))
                _logger.LogWarning(
                    "Outbox entry {EntryId} failed delivery but its claim was lost; another relay owns it now.",
                    claim.Entry.Id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The claim lease expires and the entry is redelivered.
            _logger.LogWarning(ex, "Could not record the delivery failure of outbox entry {EntryId}.", claim.Entry.Id);
        }
    }

    // Parented to the append that produced the entry's event; a plain root when there is none.
    private static Activity? StartSendActivity(OutboxEntry entry)
    {
        if (!AlbertoMetrics.Source.HasListeners())
            return null;

        var activity = AlbertoMetrics.Source.StartActivity(
            $"send {entry.MessageType}",
            ActivityKind.Producer,
            TraceContextPropagation.Extract(entry.Metadata));
        if (activity is null)
            return null;

        activity.SetTag("messaging.system", "alberto");
        activity.SetTag("messaging.operation.type", "send");
        activity.SetTag("messaging.message.id", entry.Id.ToString());
        activity.SetTag("alberto.outbox.attempt", entry.RetryCount + 1);
        if (entry.TenantId is not null)
            activity.SetTag("alberto.tenant.id", entry.TenantId);
        return activity;
    }

    private static ExternalMessage BuildMessage(OutboxEntry entry, Activity? activity)
    {
        var metadata = entry.Metadata;
        if (activity is not null)
        {
            // The transport carries the send span, not the append it descends from.
            metadata = new Dictionary<string, string>(metadata);
            TraceContextPropagation.Inject(activity, metadata);
        }

        return new ExternalMessage(
            entry.MessageType,
            entry.Version,
            entry.Payload,
            metadata,
            Destination: entry.Destination,
            RoutingHint: entry.RoutingHint)
        {
            MessageId = entry.Id,
            TenantId = entry.TenantId,
            SourceEventId = entry.SourceEventId,
        };
    }

    private async Task<bool> MarkDeliveredSafelyAsync(OutboxClaim claim)
    {
        // Deliberately not the host's token: the message is already published, so a token
        // cancelled by shutdown would leave it to be delivered a second time.
        using var timeout = new CancellationTokenSource(MarkDeliveredTimeout);
        try
        {
            if (await _store.MarkDeliveredAsync(claim, timeout.Token))
                return true;

            _logger.LogWarning(
                "Outbox entry {EntryId} was published but its claim was lost; it may be delivered again.",
                claim.Entry.Id);
        }
        catch (Exception ex)
        {
            // Published, so it must not be marked failed. The claim lease expires and the entry is
            // redelivered, which is the at-least-once contract.
            _logger.LogWarning(ex, "Outbox entry {EntryId} was published but could not be marked delivered.", claim.Entry.Id);
        }

        return false;
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
