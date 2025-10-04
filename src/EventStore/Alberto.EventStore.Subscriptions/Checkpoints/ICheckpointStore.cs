namespace Alberto.EventStore.Subscriptions.Checkpoints;

/// <summary>
/// Store for managing subscription checkpoints
/// </summary>
public interface ICheckpointStore
{
    ValueTask<Checkpoint> GetLastCheckpoint(string subscriptionId, CancellationToken ct);
    ValueTask<Checkpoint> StoreCheckpoint(Checkpoint checkpoint, CancellationToken ct);
}