using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Checkpoints;

/// <summary>
/// Checkpoint store that throttles database writes by batching updates.
/// Updates are cached in-memory and periodically flushed to the underlying store.
/// This significantly reduces database load during high-throughput scenarios.
/// </summary>
public sealed class ThrottledCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CheckpointState> _cache = new();
    private readonly Timer _flushTimer;
    private readonly ICheckpointStore _innerStore;
    private readonly ILogger<ThrottledCheckpointStore> _logger;
    private bool _disposed;

    public ThrottledCheckpointStore(
        ICheckpointStore innerStore,
        TimeSpan flushInterval,
        ILogger<ThrottledCheckpointStore> logger)
    {
        _innerStore = innerStore;
        _logger = logger;

        // Start periodic flush timer
        _flushTimer = new Timer(
            _ => FlushPendingCheckpoints().GetAwaiter().GetResult(),
            null,
            flushInterval,
            flushInterval
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the timer
        await _flushTimer.DisposeAsync();

        // Flush any remaining dirty checkpoints
        _logger.LogInformation("Flushing checkpoints before shutdown");
        await FlushPendingCheckpoints();
    }

    public async ValueTask<Checkpoint> GetLastCheckpoint(
        string subscriptionId,
        CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(subscriptionId, out var state))
        {
            return state.Checkpoint;
        }

        // Load from underlying store
        var checkpoint = await _innerStore.GetLastCheckpoint(subscriptionId, ct);

        // Cache it
        _cache.TryAdd(subscriptionId, new CheckpointState(checkpoint, false));

        return checkpoint;
    }

    public ValueTask<Checkpoint> StoreCheckpoint(
        Checkpoint checkpoint,
        CancellationToken ct)
    {
        // Update in-memory cache and mark as dirty
        var updatedCheckpoint = checkpoint with { UpdatedAt = DateTimeOffset.UtcNow };
        _cache.AddOrUpdate(
            checkpoint.SubscriptionId,
            new CheckpointState(updatedCheckpoint, true),
            (_, _) => new CheckpointState(updatedCheckpoint, true)
        );

        // Actual persistence happens on timer tick
        return ValueTask.FromResult(updatedCheckpoint);
    }

    private async Task FlushPendingCheckpoints()
    {
        if (_disposed) return;

        var dirtyCheckpoints = _cache
            .Where(kvp => kvp.Value.IsDirty)
            .Select(kvp => kvp.Value.Checkpoint)
            .ToList();

        if (dirtyCheckpoints.Count == 0)
            return;

        _logger.LogDebug(
            "Flushing {Count} pending checkpoints to database",
            dirtyCheckpoints.Count
        );

        foreach (var checkpoint in dirtyCheckpoints)
        {
            try
            {
                await _innerStore.StoreCheckpoint(checkpoint, CancellationToken.None);

                // Mark as clean after successful flush
                _cache.AddOrUpdate(
                    checkpoint.SubscriptionId,
                    new CheckpointState(checkpoint, false),
                    (_, state) => new CheckpointState(state.Checkpoint, false)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to flush checkpoint for subscription {SubscriptionId}",
                    checkpoint.SubscriptionId
                );
                // Keep as dirty for retry on next flush
            }
        }
    }

    private record CheckpointState(Checkpoint Checkpoint, bool IsDirty);
}