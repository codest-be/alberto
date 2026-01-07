namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Storage abstraction for processor checkpoints.
/// Each processor tracks its own position independently using its ProcessorId as the key.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Gets the last processed position for a processor.
    /// Returns null if the processor has never processed any events.
    /// </summary>
    Task<long?> GetAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Saves the last processed position for a processor.
    /// </summary>
    Task SaveAsync(string processorId, long position, CancellationToken ct = default);

    /// <summary>
    /// Resets the checkpoint for a processor, allowing it to reprocess from the beginning.
    /// </summary>
    Task ResetAsync(string processorId, CancellationToken ct = default);
}
