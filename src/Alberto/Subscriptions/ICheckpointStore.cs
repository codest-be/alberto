namespace Alberto.Subscriptions;

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
    /// <remarks>
    /// <para>
    /// This method is monotonic: a position lower than the current checkpoint is silently
    /// discarded (Postgres via <c>GREATEST</c>, in-memory via an explicit comparison).
    /// <see cref="RewindAsync"/> is the only way to move a checkpoint backwards.
    /// </para>
    /// <para>
    /// Some implementations buffer the update in memory and flush to durable storage on a
    /// periodic timer or via an explicit <c>FlushAsync</c>; callers requiring immediate
    /// durability must flush explicitly on such an implementation, since this interface
    /// offers no way to demand it.
    /// </para>
    /// </remarks>
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
