using System.Collections.Concurrent;
using Alberto.EventStore.Subscriptions.Checkpoints;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.InMemory.Subscriptions.Checkpoints;

/// <summary>
/// In-memory implementation of checkpoint store for testing and development
/// </summary>
public sealed class InMemoryCheckpointStore(ILogger<InMemoryCheckpointStore> logger) : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, Checkpoint> _checkpoints = new();

    public ValueTask<Checkpoint> GetLastCheckpoint(string subscriptionId, CancellationToken ct)
    {
        var checkpoint = _checkpoints.GetOrAdd(subscriptionId, _ =>
        {
            var newCheckpoint = new Checkpoint(subscriptionId, null, DateTimeOffset.UtcNow);
            logger.LogInformation(
                "Created new checkpoint for subscription '{SubscriptionId}'",
                subscriptionId
            );
            return newCheckpoint;
        });

        return ValueTask.FromResult(checkpoint);
    }

    public ValueTask<Checkpoint> StoreCheckpoint(Checkpoint checkpoint, CancellationToken ct)
    {
        var updatedCheckpoint = checkpoint with { UpdatedAt = DateTimeOffset.UtcNow };

        _checkpoints.AddOrUpdate(
            checkpoint.SubscriptionId,
            updatedCheckpoint,
            (_, _) => updatedCheckpoint
        );

        logger.LogDebug(
            "Stored checkpoint for subscription '{SubscriptionId}' at position {Position}",
            checkpoint.SubscriptionId,
            checkpoint.Position
        );

        return ValueTask.FromResult(updatedCheckpoint);
    }
}