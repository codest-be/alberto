namespace Alberto.Checkpoints;

/// <summary>
/// Set-based reads and writes for <see cref="CachingCheckpointStore"/>, so a flush or resync of
/// N processors costs one round trip instead of N (or 2N with reset detection). Internal and
/// optional: a store that does not implement it gets the per-processor path, which behaves the
/// same.
/// </summary>
internal interface ICheckpointBatchStore
{
    /// <summary>The stored checkpoint of each id; an id with no row maps to null.</summary>
    Task<IReadOnlyDictionary<string, long?>> GetManyAsync(
        IReadOnlyCollection<string> processorIds, CancellationToken cancellationToken);

    /// <summary>
    /// Writes each checkpoint monotonically (<c>GREATEST</c>), in one statement. A write whose
    /// <see cref="CheckpointWrite.Floor"/> is set lands only while the stored row is still at or
    /// above it; otherwise an operator reset or rewind happened and the write reports
    /// <see cref="CheckpointWriteResult.Reset"/> instead. With <paramref name="fencing"/> each
    /// write is also guarded by the lease, exactly as
    /// <see cref="IFencedCheckpointStore.SaveIfLeaseHeldAsync"/> guards one.
    /// </summary>
    /// <remarks>Processor ids must be distinct.</remarks>
    Task<IReadOnlyDictionary<string, CheckpointWriteOutcome>> SaveManyAsync(
        IReadOnlyList<CheckpointWrite> writes, FencingContext? fencing, CancellationToken cancellationToken);
}

/// <param name="ProcessorId">The processor.</param>
/// <param name="Position">The position to write.</param>
/// <param name="Floor">
/// The position this replica last persisted, or null when it has none. A stored row below it (or
/// gone) means an operator moved the checkpoint back, and the write must not undo that.
/// </param>
/// <param name="FenceToken">The lease generation to present when the write is fenced.</param>
internal readonly record struct CheckpointWrite(string ProcessorId, long Position, long? Floor, long FenceToken);

internal enum CheckpointWriteResult
{
    /// <summary>The checkpoint was written.</summary>
    Written,

    /// <summary>The stored row is below the floor: an external reset or rewind, not written.</summary>
    Reset,

    /// <summary>The lease is not held in the presented generation, not written.</summary>
    Fenced,

    /// <summary>Lost a race with a concurrent writer, not written; try again on the next flush.</summary>
    Retry,
}

/// <param name="Result">What happened to the write.</param>
/// <param name="StoredPosition">The stored checkpoint, for <see cref="CheckpointWriteResult.Reset"/>.</param>
internal readonly record struct CheckpointWriteOutcome(CheckpointWriteResult Result, long? StoredPosition = null);
