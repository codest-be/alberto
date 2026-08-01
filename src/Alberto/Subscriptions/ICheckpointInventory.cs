namespace Alberto.Subscriptions;

/// <summary>
/// An optional capability on an <see cref="ICheckpointStore"/>: enumerating the processor ids
/// that currently have a stored position.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ICheckpointStore"/> so a custom store keeps working without it.
/// Alberto uses it to detect checkpoints left behind by a renamed processor; a store that does
/// not implement it simply opts out of that check.
/// </remarks>
public interface ICheckpointInventory
{
    /// <summary>Returns every processor id with a stored checkpoint, in no particular order.</summary>
    Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default);
}
