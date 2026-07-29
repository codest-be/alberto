using HotChocolate;
using HotChocolate.Subscriptions;
using HotChocolate.Types;

namespace Alberto.Dcb.Admin.GraphQL;

/// <summary>
/// GraphQL mutations for the Alberto admin surface.
/// All mutations delegate to the <see cref="IAdminOperator"/> for the module named by the
/// <c>module</c> argument (or the default module) and carry an operator ID for audit.
/// Each mutation publishes to that module's subscription topics after completing.
/// </summary>
public static class AdminMutations
{
    private const string DefaultOperatorId = "admin-panel";

    /// <summary>Sets a processor checkpoint to a specific position.</summary>
    [Mutation]
    [GraphQLDescription("Set a processor checkpoint to a specific position. Appends an audit event.")]
    public static async Task<CheckpointMutationResult> AdminSetCheckpoint(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        long position,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        await modules.GetOperator(key).SetCheckpointAsync(processorId, position, opId, ct);
        var result = new CheckpointMutationResult(processorId, true);
        await PublishCheckpointAndAudit(modules, key, sender, result, "CheckpointSet", opId,
            $"Set {processorId} to position {position}", ct);
        return result;
    }

    /// <summary>Resets (deletes) a processor checkpoint, triggering full replay.</summary>
    [Mutation]
    [GraphQLDescription("Delete a processor checkpoint entirely. The processor replays from position 0.")]
    public static async Task<CheckpointMutationResult> AdminResetCheckpoint(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        await modules.GetOperator(key).ResetCheckpointAsync(processorId, opId, ct);
        var result = new CheckpointMutationResult(processorId, true);
        await PublishCheckpointAndAudit(modules, key, sender, result, "CheckpointReset", opId,
            $"Reset {processorId}", ct);
        return result;
    }

    /// <summary>Clears all dead letters across every processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries across every processor.")]
    public static async Task<DeadLetterClearResult> AdminClearAllDeadLetters(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var count = await modules.GetOperator(key).ClearAllDeadLettersAsync(opId, ct);
        var result = new DeadLetterClearResult(count);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.DeadLettersChanged, key), result, ct);
        await PublishAudit(modules, key, sender, "DeadLettersCleared", opId,
            $"Cleared {count} dead letters across all processors", ct);
        return result;
    }

    /// <summary>Clears dead letters for a specific processor.</summary>
    [Mutation]
    [GraphQLDescription("Remove all dead letter entries for a specific processor.")]
    public static async Task<DeadLetterClearResult> AdminClearDeadLettersForProcessor(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var count = await modules.GetOperator(key)
            .ClearDeadLettersForProcessorAsync(processorId, opId, ct);
        var result = new DeadLetterClearResult(count);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.DeadLettersChanged, key), result, ct);
        await PublishAudit(modules, key, sender, "DeadLettersCleared", opId,
            $"Cleared {count} dead letters for {processorId}", ct);
        return result;
    }

    /// <summary>Retries dead letters by rewinding the processor checkpoint.</summary>
    [Mutation]
    [GraphQLDescription("Atomically rewind a processor checkpoint to before its earliest dead letter, then clear all dead letters.")]
    public static async Task<RetryByRewindMutationResult> AdminRetryByRewind(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var result = await modules.GetOperator(key).RetryByRewindAsync(processorId, opId, ct);
        var mutation = new RetryByRewindMutationResult(processorId, result.RewindPosition, result.DeletedCount);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.CheckpointUpdated, key),
            new CheckpointMutationResult(processorId, true), ct);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.DeadLettersChanged, key),
            new DeadLetterClearResult(result.DeletedCount), ct);
        await PublishAudit(modules, key, sender, "RetryByRewind", opId,
            $"Rewound {processorId} to {result.RewindPosition}, cleared {result.DeletedCount} dead letters", ct);
        return mutation;
    }

    /// <summary>Releases tenant leases.</summary>
    [Mutation]
    [GraphQLDescription("Release tenant leases, forcing the application to reacquire them.")]
    public static async Task<TenantLeaseReleaseResult> AdminReleaseTenantLeases(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string? consumerId,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var count = await modules.GetOperator(key).ReleaseTenantLeasesAsync(consumerId, opId, ct);
        var result = new TenantLeaseReleaseResult(count);
        await PublishAudit(modules, key, sender, "TenantLeasesReleased", opId,
            $"Released {count} tenant leases" + (consumerId != null ? $" for {consumerId}" : ""), ct);
        return result;
    }

    /// <summary>Starts a zero-downtime projection rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Start a zero-downtime projection rebuild. The rebuild runs in the application.")]
    public static async Task<RebuildStartResult> AdminStartRebuild(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string projectionType,
        long targetPosition,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var result = await modules.GetOperator(key)
            .StartRebuildAsync(processorId, projectionType, targetPosition, opId, ct);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.RebuildUpdated, key),
            new AdminRebuildEvent(processorId, "Started", "Rebuilding"), ct);
        await PublishAudit(modules, key, sender, "RebuildStarted", opId,
            $"Started rebuild for {processorId} ({projectionType}) targeting position {targetPosition}", ct);
        return result;
    }

    /// <summary>Promotes a finished rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Promote a finished rebuild, making it the active version.")]
    public static async Task<RebuildPromoteResult> AdminPromoteRebuild(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        string? module,
        bool force = false,
        CancellationToken ct = default)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var result = await modules.GetOperator(key).PromoteRebuildAsync(processorId, force, opId, ct);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.RebuildUpdated, key),
            new AdminRebuildEvent(processorId, "Promoted", result.Status), ct);
        await PublishAudit(modules, key, sender, "RebuildPromoted", opId,
            $"Promoted rebuild for {processorId} (force={force})", ct);
        return result;
    }

    /// <summary>Aborts an in-flight rebuild.</summary>
    [Mutation]
    [GraphQLDescription("Abort an in-flight rebuild and discard the partial state.")]
    public static async Task<RebuildAbortResult> AdminAbortRebuild(
        [Service] IAdminModuleRegistry modules,
        [Service] ITopicEventSender sender,
        string processorId,
        string? operatorId,
        string? module,
        CancellationToken ct)
    {
        var key = modules.ResolveKey(module);
        var opId = operatorId ?? DefaultOperatorId;
        var result = await modules.GetOperator(key).AbortRebuildAsync(processorId, opId, ct);
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.RebuildUpdated, key),
            new AdminRebuildEvent(processorId, "Aborted", result.Status), ct);
        await PublishAudit(modules, key, sender, "RebuildAborted", opId,
            $"Aborted rebuild for {processorId}", ct);
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task PublishCheckpointAndAudit(
        IAdminModuleRegistry modules, string moduleKey, ITopicEventSender sender,
        CheckpointMutationResult result, string eventType, string operatorId, string description,
        CancellationToken ct)
    {
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.CheckpointUpdated, moduleKey), result, ct);
        await PublishAudit(modules, moduleKey, sender, eventType, operatorId, description, ct);
    }

    /// <summary>
    /// Publishes the audit entry for a completed mutation, then a fresh system snapshot.
    /// </summary>
    /// <remarks>
    /// Every mutation ends here, which is exactly why the snapshot is published from this
    /// helper: the dashboard's onAdminSystemUpdated stream stays correct no matter which
    /// mutation ran, and a mutation added later cannot silently forget to refresh it.
    /// The snapshot is a re-read rather than something derived from the mutation result,
    /// so the figures it carries are the store's, not one operation's view of it.
    /// Both go to the acting module's topics only — an operator watching Payments should
    /// not see their figures twitch because someone reset an Orders checkpoint.
    /// </remarks>
    private static async Task PublishAudit(
        IAdminModuleRegistry modules, string moduleKey, ITopicEventSender sender,
        string eventType, string operatorId, string description, CancellationToken ct)
    {
        await sender.SendAsync(AdminTopics.ForModule(AdminTopics.AuditEvent, moduleKey),
            new AdminAuditEntry(eventType, operatorId, description, DateTimeOffset.UtcNow), ct);

        // Failing to refresh the dashboard must not fail a mutation that already
        // succeeded — the operator's action is done, and surfacing it as an error would
        // invite a retry of something potentially destructive.
        try
        {
            var info = await modules.GetReader(moduleKey).GetSystemInfoAsync(ct);
            await sender.SendAsync(AdminTopics.ForModule(AdminTopics.SystemUpdated, moduleKey), info, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Subscribers pick the change up on their next query or reconnect.
        }
    }
}
