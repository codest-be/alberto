namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Where a processor sits in the rebuild state machine.
/// </summary>
public enum RebuildStatus
{
    /// <summary>No rebuild is running. Either none ever has, or the last one finished.</summary>
    Idle,

    /// <summary>A shadow control loop is replaying history into the rebuilding version.</summary>
    Rebuilding,

    /// <summary>
    /// The shadow loop has reached the target position. The rebuilt version is complete
    /// and waiting to be promoted.
    /// </summary>
    Ready,

    /// <summary>The rebuilt version was promoted and is now the active one.</summary>
    Completed,

    /// <summary>The rebuild was abandoned and its partial state discarded.</summary>
    Aborted,
}

/// <summary>
/// A processor's rebuild state.
/// </summary>
/// <param name="ProcessorId">The processor being rebuilt.</param>
/// <param name="ProjectionType">
/// Key into the projection state table. Distinct from <paramref name="ProcessorId"/> because
/// state rows are keyed by projection type while checkpoints are keyed by processor.
/// </param>
/// <param name="ActiveVersion">The version readers see. Never changes except at promotion.</param>
/// <param name="RebuildingVersion">
/// The version a shadow loop is writing into, or <see langword="null"/> when no rebuild is
/// in flight. Non-null exactly when <paramref name="Status"/> is
/// <see cref="RebuildStatus.Rebuilding"/> or <see cref="RebuildStatus.Ready"/>.
/// </param>
/// <param name="Status">Where the rebuild has got to.</param>
/// <param name="StartedAt">When the current (or most recent) rebuild started.</param>
/// <param name="TargetPosition">
/// The global position the shadow loop must reach before the rebuild can be promoted.
/// Captured from the event store head at start, so the target does not recede as new
/// events arrive.
/// </param>
/// <param name="CompletedAt">When the most recent rebuild was promoted or aborted.</param>
public sealed record ProjectionRebuildState(
    string ProcessorId,
    string ProjectionType,
    int ActiveVersion,
    int? RebuildingVersion,
    RebuildStatus Status,
    DateTimeOffset? StartedAt,
    long? TargetPosition,
    DateTimeOffset? CompletedAt)
{
    /// <summary>
    /// True while a shadow loop should be running for this processor. Survives a restart:
    /// the coordinator uses this to decide whether to resume a rebuild it did not start.
    /// </summary>
    public bool IsRebuildInFlight => Status is RebuildStatus.Rebuilding or RebuildStatus.Ready;

    /// <summary>
    /// The version a projection writes to. A shadow loop writes to the rebuilding version;
    /// everything else writes to the active one.
    /// </summary>
    public int WriteVersionFor(bool isShadowLoop)
        => isShadowLoop && RebuildingVersion is { } v ? v : ActiveVersion;
}

/// <summary>
/// The result of ending a rebuild, either by promoting it or by aborting it.
/// </summary>
/// <param name="State">The processor's state after the transition.</param>
/// <param name="DiscardedVersion">
/// The version that is no longer reachable: the superseded one after a promotion, the
/// abandoned one after an abort. State rows in <c>alberto_projection_states</c> are already
/// gone — the transition deleted them in its own transaction. Backends that keep state
/// elsewhere, EF projections in particular, still have to be told, which is what
/// <see cref="IProjectionStateClearer.ClearVersionAsync"/> is for.
/// </param>
public sealed record RebuildOutcome(ProjectionRebuildState State, int DiscardedVersion);

/// <summary>
/// Thrown when a rebuild operation is attempted from a state that does not permit it —
/// starting a second rebuild for a processor that already has one in flight, or promoting
/// one that has not finished replaying.
/// </summary>
public sealed class RebuildStateException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public RebuildStateException(string message) : base(message) { }
}

/// <summary>
/// Reads and drives the projection rebuild state machine.
/// </summary>
/// <remarks>
/// Every transition is atomic and guarded against the state it is leaving, so two operators
/// racing on the same processor cannot both win. Callers do not need to read-then-write.
/// </remarks>
public interface IProjectionRebuildStore
{
    /// <summary>
    /// Returns the rebuild state for a processor. A processor that has never been rebuilt
    /// has no row; it reports as <see cref="RebuildStatus.Idle"/> at active version 1 rather
    /// than as a missing value, so callers have one shape to handle.
    /// </summary>
    Task<ProjectionRebuildState> GetAsync(
        string processorId, string projectionType, CancellationToken ct = default);

    /// <summary>
    /// Returns the rebuild state of every processor that has one recorded.
    /// </summary>
    Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts a rebuild: allocates the next version, records the target position, and moves
    /// the processor to <see cref="RebuildStatus.Rebuilding"/>.
    /// </summary>
    /// <exception cref="RebuildStateException">A rebuild is already in flight for this processor.</exception>
    Task<ProjectionRebuildState> StartAsync(
        string processorId, string projectionType, long targetPosition, CancellationToken ct = default);

    /// <summary>
    /// Marks a rebuild as having reached its target position and ready to promote.
    /// Called by the coordinator, not by operators.
    /// </summary>
    /// <exception cref="RebuildStateException">No rebuild is in flight for this processor.</exception>
    Task<ProjectionRebuildState> MarkReadyAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Promotes the rebuilt version to active and discards the superseded one.
    /// The version flip and the cleanup of the old state rows happen in one transaction, so
    /// readers move from a complete old version to a complete new one with nothing in between.
    /// </summary>
    /// <param name="processorId">The processor to promote.</param>
    /// <param name="force">
    /// Promote from <see cref="RebuildStatus.Rebuilding"/> as well as
    /// <see cref="RebuildStatus.Ready"/>. This publishes a projection that has not finished
    /// replaying, so it is an operator override rather than a normal path.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RebuildStateException">
    /// The rebuild has not reached <see cref="RebuildStatus.Ready"/> and <paramref name="force"/>
    /// was not set, or no rebuild is in flight at all.
    /// </exception>
    Task<RebuildOutcome> PromoteAsync(
        string processorId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Abandons an in-flight rebuild and deletes the partial state it wrote. The active
    /// version is untouched, so readers never notice.
    /// </summary>
    /// <exception cref="RebuildStateException">No rebuild is in flight for this processor.</exception>
    Task<RebuildOutcome> AbortAsync(string processorId, CancellationToken ct = default);
}
