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

/// <summary>Result of promoting a projection rebuild.</summary>
public sealed record RebuildPromoteResult(string ProcessorId, string Status);

/// <summary>Result of aborting a projection rebuild.</summary>
public sealed record RebuildAbortResult(string ProcessorId, string Status);
