namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Deletes one version's worth of a projection's state.
/// Implementations are registered automatically when using <c>AddEfProjection</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is how a rebuild cleans up after itself. Promotion discards the version it
/// superseded; abort discards the partial version it abandoned. Both delete a specific
/// version rather than the whole table, because the other version is live and being read.
/// </para>
/// <para>
/// Only backends that store projection state outside <c>alberto_projection_states</c> need a
/// clearer — that table's own cleanup happens inside the promotion transaction, where it can
/// be atomic with the version flip. EF projections live in the consumer's tables, which the
/// rebuild store cannot reach, so they are cleared through this interface instead.
/// </para>
/// </remarks>
public interface IProjectionStateClearer
{
    /// <summary>
    /// The processor ID this clearer is associated with.
    /// </summary>
    string ProcessorId { get; }

    /// <summary>
    /// Deletes every row this projection wrote at <paramref name="rebuildVersion"/>, leaving
    /// every other version untouched.
    /// </summary>
    /// <remarks>
    /// Must be idempotent. It runs outside the promotion transaction, so a coordinator that
    /// crashes mid-cleanup will call it again for the same version on restart.
    /// </remarks>
    Task ClearVersionAsync(int rebuildVersion, CancellationToken ct = default);
}
