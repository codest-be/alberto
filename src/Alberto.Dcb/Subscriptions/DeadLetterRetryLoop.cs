using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Runs a dedicated loop for reprocessing dead-lettered events marked for retry via CLI.
/// Separate from the main ControlLoop to ensure fast retry turnaround (default 1 minute interval).
/// Uses SELECT...FOR UPDATE SKIP LOCKED for distributed safety across multiple service instances.
/// </summary>
public sealed class DeadLetterRetryLoop(
    IEventProcessor processor,
    IDeadLetterStore deadLetterStore,
    TimeSpan? pollingInterval = null,
    int batchSize = 10,
    IReadOnlyList<ConsumeMiddleware>? middlewares = null,
    ILogger<DeadLetterRetryLoop>? logger = null) : IHostedService, IAsyncDisposable
{
    private readonly IEventProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly IDeadLetterStore _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
    private readonly TimeSpan _pollingInterval = pollingInterval ?? TimeSpan.FromMinutes(1);
    private readonly int _batchSize = batchSize;
    private readonly IReadOnlyList<ConsumeMiddleware> _middlewares = middlewares ?? [];
    private readonly ILogger<DeadLetterRetryLoop>? _logger = logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
        _logger?.LogInformation(
            "DeadLetterRetryLoop for processor '{ProcessorId}' started (polling interval: {Interval})",
            _processor.ProcessorId,
            _pollingInterval);
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
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
        _cts?.Dispose();
        if (_loop is not null)
            await _loop;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fetch batch of retry-requested entries with distributed lock
                var retries = await _deadLetterStore.GetRetryRequestedWithLockAsync(
                    _processor.ProcessorId,
                    _batchSize,
                    ct);

                foreach (var entry in retries)
                {
                    try
                    {
                        // Remove the entry BEFORE dispatch to avoid duplicates if the retry also fails
                        await _deadLetterStore.RemoveAsync(entry.Id, ct);

                        // Reconstruct the event envelope from dead letter data
                        var envelope = entry.ToEnvelope();

                        // Dispatch through full middleware chain (retry policy applies)
                        await DispatchAsync(envelope, ct);

                        _logger?.LogInformation(
                            "DeadLetterRetryLoop successfully reprocessed event {EventId} for processor '{ProcessorId}'",
                            entry.EventId,
                            _processor.ProcessorId);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "DeadLetterRetryLoop failed to reprocess event {EventId} for processor '{ProcessorId}'. " +
                            "Processor's retry policy will handle this failure.",
                            entry.EventId,
                            _processor.ProcessorId);
                        // The processor's middleware chain (ConsumeMiddleware) will handle this failure
                        // and may create a fresh dead letter entry if exhausted
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
