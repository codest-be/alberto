using System.Collections.Concurrent;

namespace Alberto.EventStore.Subscriptions.Checkpoints;

/// <summary>
/// Ephemeral checkpoint store for channel-based subscriptions.
/// Tracks positions in-memory only - no persistence to database.
/// On restart, channel subscriptions start fresh and rely on polling to catch up missed events.
/// </summary>
public sealed class EphemeralCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, long?> _positions = new();

    public ValueTask<Checkpoint> GetLastCheckpoint(
        string subscriptionId,
        CancellationToken ct)
    {
        var position = _positions.GetOrAdd(subscriptionId, _ => null);
        return ValueTask.FromResult(new Checkpoint(subscriptionId, position, DateTimeOffset.UtcNow));
    }

    public ValueTask<Checkpoint> StoreCheckpoint(
        Checkpoint checkpoint,
        CancellationToken ct)
    {
        _positions.AddOrUpdate(checkpoint.SubscriptionId, checkpoint.Position, (_, _) => checkpoint.Position);
        return ValueTask.FromResult(checkpoint with { UpdatedAt = DateTimeOffset.UtcNow });
    }
}