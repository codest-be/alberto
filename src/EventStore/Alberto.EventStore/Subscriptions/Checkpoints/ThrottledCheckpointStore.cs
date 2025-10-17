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
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Task _flushTask;
    private readonly PeriodicTimer _flushTimer;
    private readonly ICheckpointStore _innerStore;
    private readonly ILogger<ThrottledCheckpointStore> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    public ThrottledCheckpointStore(
        ICheckpointStore innerStore,
        TimeSpan flushInterval,
        ILogger<ThrottledCheckpointStore> logger)
    {
        _innerStore = innerStore;
        _logger = logger;
        _flushTimer = new PeriodicTimer(flushInterval);

        // Start background flush task
        _flushTask = Task.Run(FlushLoop);
    }

    public async ValueTask DisposeAsync()
    {
        // Signal shutdown
        _shutdownCts.Cancel();

        // Wait for flush task to complete
        try
        {
            await _flushTask;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        // Perform final flush of any remaining dirty checkpoints
        _logger.LogInformation("Flushing checkpoints before shutdown");
        await FlushPendingCheckpoints(CancellationToken.None);

        // Cleanup resources
        _flushTimer.Dispose();
        _shutdownCts.Dispose();
        _flushLock.Dispose();
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

    private async Task FlushLoop()
    {
        try
        {
            while (await _flushTimer.WaitForNextTickAsync(_shutdownCts.Token))
            {
                await FlushPendingCheckpoints(_shutdownCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private async Task FlushPendingCheckpoints(CancellationToken ct)
    {
        // Use semaphore to prevent concurrent flushes
        await _flushLock.WaitAsync(ct);
        try
        {
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

            // Parallelize flushes for better throughput
            var flushTasks = dirtyCheckpoints.Select(checkpoint => FlushSingleCheckpoint(checkpoint, ct));
            await Task.WhenAll(flushTasks);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task FlushSingleCheckpoint(Checkpoint checkpoint, CancellationToken ct)
    {
        try
        {
            await _innerStore.StoreCheckpoint(checkpoint, ct);

            // Mark as clean only if the checkpoint in cache still matches what we flushed
            // This prevents marking a newer checkpoint as clean
            _cache.AddOrUpdate(
                checkpoint.SubscriptionId,
                new CheckpointState(checkpoint, false),
                (_, state) =>
                {
                    // Only mark as clean if position hasn't changed (no concurrent update)
                    if (state.Checkpoint.Position == checkpoint.Position)
                    {
                        return new CheckpointState(state.Checkpoint, false);
                    }

                    // Keep existing state if position has changed (newer update arrived)
                    return state;
                }
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

    private record CheckpointState(Checkpoint Checkpoint, bool IsDirty);
}