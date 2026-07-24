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

    /// <summary>
    /// Unconditionally sets the checkpoint to the given position, bypassing monotonicity guards.
    /// Unlike <see cref="SaveAsync"/>, this allows moving the position backwards. Intended exclusively
    /// for operator-initiated rewinds; normal processors must use <see cref="SaveAsync"/>.
    /// </summary>
    Task RewindAsync(string processorId, long position, CancellationToken ct = default);
}
