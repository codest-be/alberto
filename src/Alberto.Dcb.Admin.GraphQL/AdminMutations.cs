using HotChocolate;
using HotChocolate.Subscriptions;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL mutations for the Alberto admin surface.
/// All mutations delegate to <see cref="IAdminOperator"/> and carry an operator ID for audit.
/// Each mutation publishes to the relevant subscription topic after completing.
/// </summary>
public static class AdminMutations
{
    private const string DefaultOperatorId = "admin-panel";

    /// <summary>Sets a processor checkpoint to a specific position.</summary>
    [Mutation]
    [GraphQLDescription("Set a processor checkpoint to a specific position. Appends an audit event.")]
    public static async Task<CheckpointMutationResult> AdminSetCheckpoint(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        long position,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        await op.SetCheckpointAsync(processorId, position, opId, ct);
        var result = new CheckpointMutationResult(processorId, true);
        await PublishCheckpointAndAudit(sender, result, "CheckpointSet", opId,
            $"Set {processorId} to position {position}", ct);
        return result;
    }

    /// <summary>Resets (deletes) a processor checkpoint, triggering full replay.</summary>
    [Mutation]
    [GraphQLDescription("Delete a processor checkpoint entirely. The processor replays from position 0.")]
    public static async Task<CheckpointMutationResult> AdminResetCheckpoint(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        await op.ResetCheckpointAsync(processorId, opId, ct);
        var result = new CheckpointMutationResult(processorId, true);
        await PublishCheckpointAndAudit(sender, result, "CheckpointReset", opId,
            $"Reset {processorId}", ct);
        return result;
    }

    /// <summary>Clears all dead letters across every processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries across every processor.")]
    public static async Task<DeadLetterClearResult> AdminClearAllDeadLetters(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var count = await op.ClearAllDeadLettersAsync(opId, ct);
        var result = new DeadLetterClearResult(count);
        await sender.SendAsync(AdminTopics.DeadLettersChanged, result, ct);
        await PublishAudit(sender, "DeadLettersCleared", opId,
            $"Cleared {count} dead letters across all processors", ct);
        return result;
    }

    /// <summary>Clears dead letters for a specific processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries for a specific processor.")]
    public static async Task<DeadLetterClearResult> AdminClearDeadLettersForProcessor(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var count = await op.ClearDeadLettersForProcessorAsync(processorId, opId, ct);
        var result = new DeadLetterClearResult(count);
        await sender.SendAsync(AdminTopics.DeadLettersChanged, result, ct);
        await PublishAudit(sender, "DeadLettersCleared", opId,
            $"Cleared {count} dead letters for {processorId}", ct);
        return result;
    }

    /// <summary>Retries dead letters by rewinding the processor checkpoint.</summary>
    [Mutation]
    [GraphQLDescription("Atomically rewind a processor checkpoint to before its earliest dead letter, then clear all dead letters.")]
    public static async Task<RetryByRewindMutationResult> AdminRetryByRewind(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var result = await op.RetryByRewindAsync(processorId, opId, ct);
        var mutation = new RetryByRewindMutationResult(processorId, result.RewindPosition, result.DeletedCount);
        await sender.SendAsync(AdminTopics.CheckpointUpdated,
            new CheckpointMutationResult(processorId, true), ct);
        await sender.SendAsync(AdminTopics.DeadLettersChanged,
            new DeadLetterClearResult(result.DeletedCount), ct);
        await PublishAudit(sender, "RetryByRewind", opId,
            $"Rewound {processorId} to {result.RewindPosition}, cleared {result.DeletedCount} dead letters", ct);
        return mutation;
    }

    /// <summary>Releases tenant leases.</summary>
    [Mutation]
    [GraphQLDescription("Release tenant leases, forcing the application to reacquire them.")]
    public static async Task<TenantLeaseReleaseResult> AdminReleaseTenantLeases(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string? consumerId,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var count = await op.ReleaseTenantLeasesAsync(consumerId, opId, ct);
        var result = new TenantLeaseReleaseResult(count);
        await PublishAudit(sender, "TenantLeasesReleased", opId,
            $"Released {count} tenant leases" + (consumerId != null ? $" for {consumerId}" : ""), ct);
        return result;
    }

    /// <summary>Starts a zero-downtime projection rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Start a zero-downtime projection rebuild. The rebuild runs in the application.")]
    public static async Task<RebuildStartResult> AdminStartRebuild(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        string projectionType,
        long targetPosition,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var result = await op.StartRebuildAsync(processorId, projectionType, targetPosition, opId, ct);
        await sender.SendAsync(AdminTopics.RebuildUpdated,
            new AdminRebuildEvent(processorId, "Started", "Rebuilding"), ct);
        await PublishAudit(sender, "RebuildStarted", opId,
            $"Started rebuild for {processorId} ({projectionType}) targeting position {targetPosition}", ct);
        return result;
    }

    /// <summary>Promotes a finished rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Promote a finished rebuild, making it the active version.")]
    public static async Task<RebuildPromoteResult> AdminPromoteRebuild(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        bool force = false,
        string? operatorId = null,
        CancellationToken ct = default)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var result = await op.PromoteRebuildAsync(processorId, force, opId, ct);
        await sender.SendAsync(AdminTopics.RebuildUpdated,
            new AdminRebuildEvent(processorId, "Promoted", result.Status), ct);
        await PublishAudit(sender, "RebuildPromoted", opId,
            $"Promoted rebuild for {processorId} (force={force})", ct);
        return result;
    }

    /// <summary>Aborts an in-flight rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Abort an in-flight rebuild and discard the partial state.")]
    public static async Task<RebuildAbortResult> AdminAbortRebuild(
        [Service] IAdminOperator op,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        CancellationToken ct)
    {
        var opId = operatorId ?? DefaultOperatorId;
        var result = await op.AbortRebuildAsync(processorId, opId, ct);
        await sender.SendAsync(AdminTopics.RebuildUpdated,
            new AdminRebuildEvent(processorId, "Aborted", result.Status), ct);
        await PublishAudit(sender, "RebuildAborted", opId,
            $"Aborted rebuild for {processorId}", ct);
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task PublishCheckpointAndAudit(
        ITopicEventSender sender, CheckpointMutationResult result,
        string eventType, string operatorId, string description,
        CancellationToken ct)
    {
        await sender.SendAsync(AdminTopics.CheckpointUpdated, result, ct);
        await PublishAudit(sender, eventType, operatorId, description, ct);
    }

    private static async Task PublishAudit(
        ITopicEventSender sender, string eventType, string operatorId,
        string description, CancellationToken ct) =>
        await sender.SendAsync(AdminTopics.AuditEvent,
            new AdminAuditEntry(eventType, operatorId, description, DateTimeOffset.UtcNow), ct);
}
