namespace Alberto.Subscriptions;

/// <summary>
/// An optional capability on an <see cref="ICheckpointStore"/>: enumerating the processor ids
/// that currently have a stored position.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ICheckpointStore"/> so a custom store keeps working without it.
/// Alberto uses it to detect checkpoints left behind by a renamed processor; a store that does
/// not implement it simply opts out of that check.
/// <para>
/// Implementations that wrap another store (decorators) should override <see cref="CanEnumerate"/>
/// to return <c>false</c> when their inner store does not support enumeration. Alberto queries
/// <see cref="CanEnumerate"/> at the resolution site so that a decorator with an incapable inner
/// is treated identically to a store that never implemented the interface — neither causes a
/// false all-clear under the strict orphan-checkpoint policy.
/// </para>
/// </remarks>
public interface ICheckpointInventory
{
    /// <summary>
    /// Whether this instance can actually enumerate checkpoint ids right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <c>true</c>: a store that implements this interface is presumed to
    /// enumerate unconditionally. Override and return <c>false</c> only when the instance
    /// cannot answer the question — for example, a decorator whose inner store does not
    /// implement <see cref="ICheckpointInventory"/>. Returning <c>false</c> opts the store
    /// out of the orphaned-checkpoint check in the same way as not implementing the interface.
    /// </para>
    /// <para>
    /// Callers must check this property before invoking <see cref="ListProcessorIdsAsync"/>.
    /// An empty list returned by a store with <c>CanEnumerate == false</c> is meaningless
    /// and would be indistinguishable from a genuine all-clear.
    /// </para>
    /// </remarks>
    bool CanEnumerate => true;

    /// <summary>Returns every processor id with a stored checkpoint, in no particular order.</summary>
    Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default);
}
