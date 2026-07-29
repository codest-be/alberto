using HotChocolate;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL mutations for the Alberto admin surface.
/// All mutations delegate to <see cref="IAdminOperator"/> and carry an operator ID for audit.
/// </summary>
public static class AdminMutations
{
    private const string DefaultOperatorId = "admin-panel";

    /// <summary>Sets a processor checkpoint to a specific position.</summary>
    [Mutation]
    [GraphQLDescription("Set a processor checkpoint to a specific position. Appends an audit event.")]
    public static async Task<CheckpointMutationResult> AdminSetCheckpoint(
        [Service] IAdminOperator op,
        string processorId,
        long position,
        string? operatorId,
        CancellationToken ct)
    {
        await op.SetCheckpointAsync(processorId, position, operatorId ?? DefaultOperatorId, ct);
        return new CheckpointMutationResult(processorId, true);
    }

    /// <summary>Resets (deletes) a processor checkpoint, triggering full replay.</summary>
    [Mutation]
    [GraphQLDescription("Delete a processor checkpoint entirely. The processor replays from position 0.")]
    public static async Task<CheckpointMutationResult> AdminResetCheckpoint(
        [Service] IAdminOperator op,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        await op.ResetCheckpointAsync(processorId, operatorId ?? DefaultOperatorId, ct);
        return new CheckpointMutationResult(processorId, true);
    }

    /// <summary>Clears all dead letters across every processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries across every processor.")]
    public static async Task<DeadLetterClearResult> AdminClearAllDeadLetters(
        [Service] IAdminOperator op,
        string? operatorId,
        CancellationToken ct)
    {
        var count = await op.ClearAllDeadLettersAsync(operatorId ?? DefaultOperatorId, ct);
        return new DeadLetterClearResult(count);
    }

    /// <summary>Clears dead letters for a specific processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries for a specific processor.")]
    public static async Task<DeadLetterClearResult> AdminClearDeadLettersForProcessor(
        [Service] IAdminOperator op,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var count = await op.ClearDeadLettersForProcessorAsync(processorId, operatorId ?? DefaultOperatorId, ct);
        return new DeadLetterClearResult(count);
    }

    /// <summary>Retries dead letters by rewinding the processor checkpoint.</summary>
    [Mutation]
    [GraphQLDescription("Atomically rewind a processor checkpoint to before its earliest dead letter, then clear all dead letters.")]
    public static async Task<RetryByRewindMutationResult> AdminRetryByRewind(
        [Service] IAdminOperator op,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var result = await op.RetryByRewindAsync(processorId, operatorId ?? DefaultOperatorId, ct);
        return new RetryByRewindMutationResult(processorId, result.RewindPosition, result.DeletedCount);
    }

    /// <summary>Releases tenant leases.</summary>
    [Mutation]
    [GraphQLDescription("Release tenant leases, forcing the application to reacquire them.")]
    public static async Task<TenantLeaseReleaseResult> AdminReleaseTenantLeases(
        [Service] IAdminOperator op,
        string? consumerId,
        string? operatorId,
        CancellationToken ct)
    {
        var count = await op.ReleaseTenantLeasesAsync(consumerId, operatorId ?? DefaultOperatorId, ct);
        return new TenantLeaseReleaseResult(count);
    }

    /// <summary>Starts a zero-downtime projection rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Start a zero-downtime projection rebuild. The rebuild runs in the application.")]
    public static async Task<RebuildStartResult> AdminStartRebuild(
        [Service] IAdminOperator op,
        string processorId,
        string projectionType,
        long targetPosition,
        string? operatorId,
        CancellationToken ct) =>
        await op.StartRebuildAsync(processorId, projectionType, targetPosition,
            operatorId ?? DefaultOperatorId, ct);

    /// <summary>Promotes a finished rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Promote a finished rebuild, making it the active version.")]
    public static async Task<RebuildPromoteResult> AdminPromoteRebuild(
        [Service] IAdminOperator op,
        string processorId,
        bool force = false,
        string? operatorId = null,
        CancellationToken ct = default) =>
        await op.PromoteRebuildAsync(processorId, force, operatorId ?? DefaultOperatorId, ct);

    /// <summary>Aborts an in-flight rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Abort an in-flight rebuild and discard the partial state.")]
    public static async Task<RebuildAbortResult> AdminAbortRebuild(
        [Service] IAdminOperator op,
        string processorId,
        string? operatorId,
        CancellationToken ct) =>
        await op.AbortRebuildAsync(processorId, operatorId ?? DefaultOperatorId, ct);
}
