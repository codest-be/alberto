using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Zero-downtime projection rebuilds. The operator records intent; the running application's
/// rebuild coordinator performs the actual replay and version swap on its next poll.
/// </summary>
/// <remarks>
/// Requires the host application to opt in with
/// <c>.WithControlLoop(loop =&gt; loop.WithRebuilds())</c>. Without it these tools record intent
/// that nothing will ever act on.
/// </remarks>
[McpServerToolType]
public sealed class RebuildTools(IAdminReader reader, IAdminOperator op)
{
    [McpServerTool(Name = "alberto_rebuild_status", ReadOnly = true)]
    [Description(
        "Reports rebuild state per projection: which version is live, which one (if any) is being " +
        "built, how far the shadow replay has got, and what it is aiming for. Poll this after " +
        "alberto_start_rebuild — replayedPosition climbing toward targetPosition is progress, and " +
        "reaching it is the signal that alberto_promote_rebuild will succeed. Returns an empty " +
        "list for a processor that has never had a rebuild started.")]
    public Task<List<RebuildStateInfo>> GetRebuildStatusAsync(
        [Description("Restrict to a single processor. Omit to report every projection.")] string? processorId = null,
        CancellationToken ct = default) =>
        reader.GetRebuildStatesAsync(processorId, ct);

    [McpServerTool(Name = "alberto_start_rebuild", Destructive = false)]
    [Description(
        "Starts a zero-downtime rebuild: allocates a shadow version of the projection and records " +
        "the current log head as the target. A shadow control loop replays history into the shadow " +
        "copy while the live copy keeps serving reads, so nothing goes down. Nothing swaps until " +
        "you call alberto_promote_rebuild. Prefer this over alberto_reset_checkpoint whenever the " +
        "projection is being read by anything.")]
    public async Task<RebuildStartResult> StartRebuildAsync(
        [Description("The processor to rebuild.")] string processorId,
        [Description("Key into the projection state table. Defaults to the processor ID, which is correct unless the projection was registered under a different type name.")] string? projectionType = null,
        [Description("The global position the shadow copy must reach before it can be promoted. Defaults to the current log head.")] long? targetPosition = null,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default)
    {
        var target = targetPosition ?? await reader.GetGlobalPositionAsync(ct) ?? 0L;
        return await op.StartRebuildAsync(processorId, projectionType ?? processorId, target, operatorId, ct);
    }

    [McpServerTool(Name = "alberto_promote_rebuild", Destructive = true, Idempotent = true)]
    [Description(
        "Requests that the finished shadow copy replace the live one. The coordinator verifies the " +
        "shadow has caught up before swapping, and the swap itself is a single transaction. The " +
        "superseded version is not deleted immediately — a later sweep reclaims it once readers " +
        "holding the old version number have had time to finish.")]
    public Task<RebuildPromoteResult> PromoteRebuildAsync(
        [Description("The processor whose rebuild is promoted.")] string processorId,
        [Description("Request promotion before the original target position is reached. The coordinator still refuses to swap until the shadow is at least as current as the live processor.")] bool force = false,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.PromoteRebuildAsync(processorId, force, operatorId, ct);

    [McpServerTool(Name = "alberto_abort_rebuild", Destructive = true, Idempotent = true)]
    [Description(
        "Abandons an in-flight rebuild and discards the shadow copy. The live version is untouched, " +
        "so this is safe. The shadow loop only learns of the abort on its next poll, so a few more " +
        "writes may land afterwards; the coordinator's sweep removes them.")]
    public Task<RebuildAbortResult> AbortRebuildAsync(
        [Description("The processor whose rebuild is aborted.")] string processorId,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.AbortRebuildAsync(processorId, operatorId, ct);
}
