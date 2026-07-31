namespace Alberto.Subscriptions;

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
/// An operator intent that the running rebuild coordinator must carry out.
/// Operators record intent; only the coordinator may execute the multi-step handoff.
/// </summary>
public enum RebuildOperatorAction
{
    /// <summary>Promote after the rebuild reaches <see cref="RebuildStatus.Ready"/>.</summary>
    Promote,

    /// <summary>
    /// Promote before the original target is reached, but only after the shadow copy is at
    /// least as current as the live processor.
    /// </summary>
    ForcePromote,

    /// <summary>Stop the shadow loop and discard its version.</summary>
    Abort,
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
/// <param name="LastAllocatedVersion">
/// High-water mark of every rebuild version ever allocated. Versions are never reused.
/// </param>
/// <param name="RequestedAction">
/// Operator intent waiting for the running coordinator, or null when no action is pending.
/// </param>
public sealed record ProjectionRebuildState(
    string ProcessorId,
    string ProjectionType,
    int ActiveVersion,
    int? RebuildingVersion,
    RebuildStatus Status,
    DateTimeOffset? StartedAt,
    long? TargetPosition,
    DateTimeOffset? CompletedAt,
    int LastAllocatedVersion = 1,
    RebuildOperatorAction? RequestedAction = null)
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
/// abandoned one after an abort. Unreachable is not the same as gone — the rows survive the
/// transition and are reclaimed a grace period later, by
/// <see cref="IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync"/> for state held in
/// <c>alberto_projection_states</c> and by <see cref="IProjectionStateClearer.ClearVersionAsync"/>
/// for backends that keep it elsewhere, EF projections in particular. Deleting either of them
/// any earlier is what strands a reader holding a version number it resolved a moment before
/// the flip.
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
/// Reads the projection rebuild state machine and records operator intent.
/// </summary>
/// <remarks>
/// Ending a rebuild is a protocol, not a database row update: the shadow loop must stop,
/// checkpoints must be handed off, version selectors refreshed, and external state cleared.
/// This interface deliberately cannot perform those coordinator-only transitions.
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
    /// Records an operator request to promote. The running coordinator performs the safe
    /// handoff and clears the request when it completes.
    /// </summary>
    /// <param name="processorId">The processor to promote.</param>
    /// <param name="force">
    /// Permit promotion before the original target is reached. The coordinator still refuses
    /// to publish a shadow copy behind the live processor.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RebuildStateException">No rebuild is in flight.</exception>
    Task<ProjectionRebuildState> RequestPromotionAsync(
        string processorId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Records an operator request to abort. The running coordinator stops the shadow loop
    /// before discarding its state.
    /// </summary>
    /// <exception cref="RebuildStateException">No rebuild is in flight for this processor.</exception>
    Task<ProjectionRebuildState> RequestAbortAsync(
        string processorId, CancellationToken ct = default);
}

/// <summary>
/// Coordinator-only transitions for the rebuild protocol.
/// </summary>
/// <remarks>
/// Adapters implement this interface alongside <see cref="IProjectionRebuildStore"/>, but
/// operator modules receive only the latter. This keeps the completion seam structural.
/// </remarks>
internal interface IProjectionRebuildCoordinatorStore
{
    /// <summary>Marks a historical replay ready after its checkpoint reaches the target.</summary>
    Task<ProjectionRebuildState> MarkReadyAsync(
        string processorId, CancellationToken ct = default);

    /// <summary>
    /// Atomically flips the rebuilt version to active. The caller must already have stopped the
    /// shadow loop and verified checkpoint locality.
    /// </summary>
    /// <remarks>
    /// The superseded version's rows are deliberately left behind; see
    /// <see cref="DiscardStateVersionAsync"/> for why, and for who reclaims them.
    /// </remarks>
    Task<RebuildOutcome> CompletePromotionAsync(
        string processorId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks an abort complete. The caller must already have stopped the shadow loop.
    /// </summary>
    /// <remarks>
    /// As with <see cref="CompletePromotionAsync"/>, the abandoned version's rows outlive the
    /// transition and are reclaimed later by <see cref="DiscardStateVersionAsync"/>.
    /// </remarks>
    Task<RebuildOutcome> CompleteAbortAsync(
        string processorId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every state row a projection holds at one version, across all tenants.
    /// Reclaiming a version that holds nothing is a no-op, so this is safe to call repeatedly
    /// and safe to call for a version some other replica has already reclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is separate from the two completion transitions on purpose, and calling it too
    /// soon after one reintroduces the bug it exists to avoid. A reader resolves the active
    /// version and then queries it — two steps, and a promotion can land between them. Deleting
    /// the superseded rows inside the flip therefore makes the swap invisible only to readers
    /// that resolve *after* it: one that resolved a moment earlier is left holding a version
    /// number whose rows have just been deleted, and sees an empty projection rather than
    /// either the old copy or the new one.
    /// </para>
    /// <para>
    /// Nothing about the flip can close that window, because the reader's stale version number
    /// is already in its hand. What closes it is time: every reader refreshes its version cache
    /// within one <see cref="ProjectionVersions"/> refresh interval, so a superseded version
    /// left intact for longer than that has no readers left to disappoint. The coordinator's
    /// sweep does the reclaiming once that grace period has elapsed.
    /// </para>
    /// </remarks>
    /// <param name="projectionType">The key the projection's state rows are stored under.</param>
    /// <param name="version">The rebuild version to reclaim.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DiscardStateVersionAsync(
        string projectionType, int version, CancellationToken ct = default);
}
