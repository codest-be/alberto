using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Runs a dedicated loop for reprocessing dead-lettered events marked for retry via CLI.
/// Separate from the main ControlLoop to ensure fast retry turnaround (default 1 minute interval).
/// Each entry is claimed with a time-bounded lease before dispatch — if the worker dies mid-dispatch
/// the lease expires and another worker re-claims the entry, instead of the row being deleted up-front
/// and the event lost.
/// </summary>
public sealed class DeadLetterRetryLoop(
    IEventProcessor processor,
    IDeadLetterStore deadLetterStore,
    TimeSpan? pollingInterval = null,
    int batchSize = 10,
    IReadOnlyList<ConsumeMiddleware>? middlewares = null,
    ILogger<DeadLetterRetryLoop>? logger = null,
    TimeSpan? claimLeaseDuration = null,
    string? claimedBy = null,
    IServiceScopeFactory? scopeFactory = null) : IHostedService, IAsyncDisposable
{
    private readonly IEventProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly IDeadLetterStore _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
    private readonly TimeSpan _pollingInterval = pollingInterval ?? TimeSpan.FromMinutes(1);
    private readonly int _batchSize = batchSize;
    private readonly IReadOnlyList<ConsumeMiddleware> _middlewares = middlewares ?? [];
    private readonly ILogger<DeadLetterRetryLoop>? _logger = logger;
    private readonly TimeSpan _claimLeaseDuration = claimLeaseDuration is { } d && d > TimeSpan.Zero
        ? d
        : DefaultClaimLeaseDuration;
    private readonly string _claimedBy = string.IsNullOrWhiteSpace(claimedBy)
        ? $"{Environment.MachineName}:{Environment.ProcessId}"
        : claimedBy!;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Default lease duration for a claimed dead-letter entry. Picked to be longer than the slowest
    /// realistic handler (e.g. transcribing a long-form podcast) so a healthy worker won't lose its
    /// claim mid-dispatch, but short enough that a crashed worker's claims become available again
    /// within an operationally acceptable window.
    /// </summary>
    public static readonly TimeSpan DefaultClaimLeaseDuration = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        _logger?.LogInformation(
            "DeadLetterRetryLoop for processor '{ProcessorId}' started (polling interval: {Interval}, claim lease: {Lease}, claimed by: {ClaimedBy})",
            _processor.ProcessorId,
            _pollingInterval,
            _claimLeaseDuration,
            _claimedBy);
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }

            try
            {
                if (_loop is not null)
                    await _loop;
            }
            catch (OperationCanceledException) { }
        }
        _logger?.LogInformation("DeadLetterRetryLoop for processor '{ProcessorId}' stopped", _processor.ProcessorId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            Interlocked.Exchange(ref _cts, null)?.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Atomically claim a batch with a time-bounded lease. Until the lease expires
                // (or we explicitly delete/release the row) no other worker will pick it up.
                var retries = await _deadLetterStore.ClaimRetryRequestedAsync(
                    _processor.ProcessorId,
                    _batchSize,
                    _claimLeaseDuration,
                    _claimedBy,
                    ct);

                foreach (var entry in retries)
                {
                    var dispatchSucceeded = false;
                    try
                    {
                        // Reconstruct the event envelope from dead letter data
                        var envelope = entry.ToEnvelope();

                        // Dispatch through full middleware chain (retry policy applies). The
                        // built-in RetryAndDeadLetter middleware swallows non-cancellation failures
                        // and writes a fresh dead-letter row on exhaustion, so a normal return here
                        // covers both "handler succeeded" and "handler failed but is now in a new
                        // dead-letter row". In both cases the claimed entry is no longer needed.
                        await DispatchWithTenantScopeAsync(envelope, entry, ct);
                        dispatchSucceeded = true;

                        _logger?.LogInformation(
                            "DeadLetterRetryLoop successfully reprocessed event {EventId} for processor '{ProcessorId}'",
                            entry.EventId,
                            _processor.ProcessorId);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Shutdown: leave the claim in place. The lease will expire and another
                        // worker (or this one on restart) will re-claim and dispatch.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Custom middleware that re-throws despite RetryAndDeadLetter, or an error
                        // outside the middleware chain. The handler is still failing, so retrying on
                        // the next poll would just busy-loop. Abandon the retry: clear retry_requested
                        // and the claim columns. The row stays in the dead letter table for inspection
                        // and must be explicitly re-marked via MarkForRetryAsync to be tried again.
                        _logger?.LogWarning(ex,
                            "DeadLetterRetryLoop failed to reprocess event {EventId} for processor '{ProcessorId}'. " +
                            "Abandoning retry; entry must be re-marked manually to retry again.",
                            entry.EventId,
                            _processor.ProcessorId);
                        try
                        {
                            await _deadLetterStore.AbandonRetryAsync(entry.Id, ct);
                        }
                        catch (Exception abandonEx)
                        {
                            _logger?.LogWarning(abandonEx,
                                "DeadLetterRetryLoop failed to abandon retry on entry {EntryId}; " +
                                "lease will expire and entry may be re-dispatched.",
                                entry.Id);
                        }
                    }

                    if (dispatchSucceeded)
                    {
                        try
                        {
                            await _deadLetterStore.RemoveAsync(entry.Id, ct);
                        }
                        catch (Exception removeEx) when (!ct.IsCancellationRequested)
                        {
                            // The dispatch itself succeeded; a transient delete failure isn't
                            // catastrophic. The lease prevents anyone else from picking the row up,
                            // and once the lease expires the row will be re-dispatched (the handler
                            // is expected to be idempotent — it's a re-run of an already-failed event).
                            _logger?.LogWarning(removeEx,
                                "DeadLetterRetryLoop failed to remove entry {EntryId} after successful dispatch; " +
                                "lease will expire and the entry will be re-dispatched (handler must be idempotent).",
                                entry.Id);
                        }
                    }
                }

                // Delay before next poll if no entries were processed
                if (retries.Count == 0)
                    await Task.Delay(_pollingInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "DeadLetterRetryLoop for processor '{ProcessorId}' encountered an unexpected error. " +
                    "Will retry after delay.",
                    _processor.ProcessorId);
                await Task.Delay(_pollingInterval, ct);
            }
        }
    }

    // P0.6 (TEN-5): Create a DI scope per retry entry and set the tenant context so any
    // scoped service resolved within the dispatch (e.g. ITenantAccessor, scoped handler classes)
    // sees the correct tenant for this event.
    //
    // NOTE: _processor is a singleton — its constructor-injected ITenantAccessor was captured at
    // build time from its own construction scope and will NOT be updated by the scope created here.
    // The scope benefits code paths that explicitly resolve scoped services per-call (e.g. scoped
    // reactor handler classes wired via ReactTo<TEvent, THandler>). A complete fix would resolve
    // the processor itself from the scope using the module key — that requires DeadLetterRetryLoop
    // to know the module key, tracked as a follow-up in the breaking-changes phase.
    private async Task DispatchWithTenantScopeAsync(IEventEnvelope envelope, DeadLetterEntry entry, CancellationToken ct)
    {
        if (scopeFactory is not null && !string.IsNullOrEmpty(entry.TenantId))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tenantCtx = scope.ServiceProvider.GetService<TenantContext>();
            tenantCtx?.SetTenant(entry.TenantId);
            await DispatchAsync(envelope, ct);
        }
        else
        {
            await DispatchAsync(envelope, ct);
        }
    }

    private Task DispatchAsync(IEventEnvelope evt, CancellationToken ct)
    {
        if (_middlewares.Count == 0)
            return _processor.ProcessEventAsync(evt, ct);

        var context = new ConsumeEventContext
        {
            ProcessorId = _processor.ProcessorId,
            ModuleKey = "",
            Envelope = evt,
            IsRebuild = _processor.IsRebuilding,
            CancellationToken = ct,
        };

        return MiddlewareRunner.RunAsync(
            context,
            _middlewares,
            () => _processor.ProcessEventAsync(evt, ct));
    }
}

/// <summary>
/// Options for configuring the dead letter retry loop.
/// </summary>
public record DeadLetterRetryLoopOptions(
    TimeSpan PollingInterval = default,
    int BatchSize = 10)
{
    /// <summary>
    /// Default polling interval: 1 minute.
    /// </summary>
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMinutes(1);

    internal TimeSpan ResolvedPollingInterval => PollingInterval == default ? DefaultPollingInterval : PollingInterval;
}

/// <summary>
/// Extension methods for <see cref="DeadLetterEntry"/> to support retry operations.
/// </summary>
internal static class DeadLetterEntryExtensions
{
    /// <summary>
    /// Reconstructs an <see cref="EventEnvelope"/> from a dead letter entry for reprocessing.
    /// </summary>
    internal static EventEnvelope ToEnvelope(this DeadLetterEntry entry)
    {
        // Parse tags from "concept:id" format
        var tags = new List<EventTag>();
        if (entry.Tags != null)
        {
            foreach (var tagStr in entry.Tags)
            {
                try
                {
                    tags.Add(EventTag.Parse(tagStr));
                }
                catch
                {
                    // Skip malformed tags
                }
            }
        }

        return new EventEnvelope
        {
            Id = entry.EventId,
            TenantId = entry.TenantId,
            GlobalPosition = entry.GlobalPosition,
            EventType = new EventType(entry.EventType),
            Tags = tags,
            EventData = entry.EventData,
            Metadata = entry.Metadata ?? new Dictionary<string, string>(),
            CreatedAt = entry.CreatedAt ?? DateTime.UtcNow,
        };
    }
}
