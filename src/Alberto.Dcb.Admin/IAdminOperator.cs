namespace Alberto.Dcb.Admin;

/// <summary>
/// Mutation interface for admin operations on the Alberto event store.
///
/// <para>
/// Every mutation appends an admin audit event to the event log in the same transaction
/// as the state change (where possible). This gives every operator action a durable,
/// queryable audit trail without a separate audit table.
/// </para>
/// <para>
/// The <c>operatorId</c> parameter on each method identifies who performed the action.
/// In the CLI this defaults to <c>Environment.UserName</c>; in GraphQL it defaults to
/// <c>"admin-panel"</c>.
/// </para>
/// </summary>
public interface IAdminOperator
{
    /// <summary>
    /// Upserts a checkpoint row, setting <paramref name="processorId"/> to <paramref name="position"/>.
    /// Appends <see cref="AdminCheckpointRewound"/> in the same transaction.
    /// </summary>
    Task SetCheckpointAsync(string processorId, long position, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a checkpoint row entirely, triggering a full replay from position 0.
    /// Appends <see cref="AdminCheckpointReset"/> in the same transaction.
    /// </summary>
    Task ResetCheckpointAsync(string processorId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Moves a checkpoint from one processor id to another, carrying its position across a rename
    /// of the processor itself. Refuses rather than overwrites when the destination already has a
    /// checkpoint — see <see cref="CheckpointRenameStatus"/> for the outcomes.
    /// Appends <see cref="AdminCheckpointRenamed"/> in the same transaction when the rename happens.
    /// </summary>
    /// <remarks>
    /// <see cref="IAdminReader"/> exposes a read-only twin of this operation. This is the one that
    /// records who did it; prefer it for anything an operator triggers.
    /// </remarks>
    Task<CheckpointRenameResult> RenameCheckpointAsync(
        string fromProcessorId, string toProcessorId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Removes all dead letter entries across every processor.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> ClearAllDeadLettersAsync(string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Removes all dead letter entries for a specific processor.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> ClearDeadLettersForProcessorAsync(string processorId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Flags every dead letter for a processor so its retry loop picks them up on the next cycle.
    /// The rows stay where they are — nothing is deleted and no checkpoint moves.
    /// Returns the number of rows flagged.
    /// Appends <see cref="AdminDeadLettersMarkedForRetry"/> in the same transaction.
    /// </summary>
    Task<int> MarkDeadLettersForRetryAsync(
        string processorId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Atomically rewinds a processor checkpoint to one position before its earliest dead letter,
    /// then clears all dead letters for that processor.
    /// </summary>
    /// <returns>
    /// <c>RewindPosition</c> is the new checkpoint value; <c>DeletedCount</c> is the number of
    /// dead letters removed. <c>RewindPosition</c> is <see langword="null"/> when the processor
    /// has no dead letters.
    /// </returns>
    Task<RetryByRewindResult> RetryByRewindAsync(
        string processorId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Releases tenant leases, forcing the application to reacquire them.
    /// When <paramref name="consumerId"/> is non-null, only leases for that consumer group
    /// are released; otherwise all tenant leases are released.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> ReleaseTenantLeasesAsync(string? consumerId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Permanently removes outbox entries delivered before <paramref name="before"/>.
    /// Returns the number of rows deleted.
    /// Appends <see cref="AdminOutboxPurged"/> in the same transaction.
    /// </summary>
    /// <remarks>
    /// Only entries in the <c>delivered</c> state are eligible — a pending, processing or failed
    /// entry is undelivered work and is never removed here, however old it is. This is the manual
    /// counterpart to the retention sweep the outbox runs on a schedule; reach for it to reclaim
    /// space now, or on a store whose retention is disabled.
    /// </remarks>
    Task<int> PurgeOutboxAsync(
        DateTimeOffset before, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Starts a zero-downtime projection rebuild.
    /// </summary>
    Task<RebuildStartResult> StartRebuildAsync(
        string processorId, string projectionType, long targetPosition,
        string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Requests promotion of a finished rebuild, making it the active version.
    /// </summary>
    Task<RebuildPromoteResult> PromoteRebuildAsync(
        string processorId, bool force, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// Requests abort of an in-flight rebuild.
    /// </summary>
    Task<RebuildAbortResult> AbortRebuildAsync(
        string processorId, string operatorId, CancellationToken ct = default);
}

/// <summary>Result of a retry-by-rewind operation.</summary>
public sealed record RetryByRewindResult(long? RewindPosition, int DeletedCount);

/// <summary>Result of starting a projection rebuild.</summary>
public sealed record RebuildStartResult(int ActiveVersion, int RebuildingVersion, long TargetPosition);

/// <summary>
/// Result of promoting a projection rebuild. The versions are the ones in force when the request
/// was recorded — promotion completes on the coordinator's next poll, not here, so
/// <paramref name="ActiveVersion"/> is still the version being served.
/// </summary>
/// <param name="ProcessorId">The processor whose rebuild was asked to promote.</param>
/// <param name="Status">The rebuild's status as recorded by the request.</param>
/// <param name="ActiveVersion">
/// The version currently being served. <c>0</c> when the caller did not report one — versions are
/// allocated from 1, so 0 never collides with a real version.
/// </param>
/// <param name="RebuildingVersion">The shadow version awaiting promotion, or null when there is none.</param>
public sealed record RebuildPromoteResult(
    string ProcessorId, string Status, int ActiveVersion = 0, int? RebuildingVersion = null);

/// <summary>
/// Result of aborting a projection rebuild. As with promotion, the abort takes effect on the
/// coordinator's next poll, so these are the versions as they stood when the request was recorded.
/// </summary>
/// <param name="ProcessorId">The processor whose rebuild was asked to abort.</param>
/// <param name="Status">The rebuild's status as recorded by the request.</param>
/// <param name="ActiveVersion">
/// The version currently being served. <c>0</c> when the caller did not report one — versions are
/// allocated from 1, so 0 never collides with a real version.
/// </param>
/// <param name="RebuildingVersion">The shadow version being discarded, or null when there is none.</param>
public sealed record RebuildAbortResult(
    string ProcessorId, string Status, int ActiveVersion = 0, int? RebuildingVersion = null);
