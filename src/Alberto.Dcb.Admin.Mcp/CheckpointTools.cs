using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Processor and checkpoint inspection plus the operator escape hatches that move a
/// checkpoint. Every mutation here changes what a running processor will replay.
/// </summary>
[McpServerToolType]
public sealed class CheckpointTools(IAdminReader reader, IAdminOperator op)
{
    [McpServerTool(Name = "alberto_list_processors", ReadOnly = true)]
    [Description(
        "Every registered processor with its checkpoint position and when that position last " +
        "advanced. A processor whose position lags far behind the global position is falling behind.")]
    public Task<List<ProcessorInfo>> ListProcessorsAsync(CancellationToken ct = default) =>
        reader.GetProcessorsAsync(ct);

    [McpServerTool(Name = "alberto_list_checkpoints", ReadOnly = true)]
    [Description(
        "All processor checkpoints ordered by processor ID: the last global position each " +
        "processor consumed, and when it was updated.")]
    public Task<List<CheckpointInfo>> ListCheckpointsAsync(CancellationToken ct = default) =>
        reader.GetCheckpointsAsync(ct);

    [McpServerTool(Name = "alberto_get_checkpoint", ReadOnly = true)]
    [Description("The checkpoint for one processor, or null when that processor has none recorded.")]
    public Task<CheckpointInfo?> GetCheckpointAsync(
        [Description("The processor ID, e.g. 'orders-projection'.")] string processorId,
        CancellationToken ct = default) =>
        reader.GetSingleCheckpointAsync(processorId, ct);

    [McpServerTool(Name = "alberto_set_checkpoint", Destructive = true, Idempotent = true)]
    [Description(
        "DESTRUCTIVE. Moves a processor's checkpoint to an explicit global position. Rewinding " +
        "makes the processor re-consume every event after that position — handlers must be " +
        "idempotent or side effects will be duplicated. Fast-forwarding skips events permanently. " +
        "This is the only way to move a checkpoint backwards; normal checkpoint writes are " +
        "monotonic. Positions are per-database, so never copy a position between shards. " +
        "Confirm the position with alberto_get_checkpoint before calling this.")]
    public async Task<CheckpointInfo?> SetCheckpointAsync(
        [Description("The processor whose checkpoint moves.")] string processorId,
        [Description("The global position to set. The processor resumes from the event after this one.")] long position,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default)
    {
        await op.SetCheckpointAsync(processorId, position, operatorId, ct);
        return await reader.GetSingleCheckpointAsync(processorId, ct);
    }

    [McpServerTool(Name = "alberto_reset_checkpoint", Destructive = true, Idempotent = true)]
    [Description(
        "DESTRUCTIVE. Deletes a processor's checkpoint row entirely, so on its next poll it " +
        "replays the whole log from position 0. On a large store this can take a long time and " +
        "will re-run every side effect the processor performs. For rebuilding a projection " +
        "without downtime, prefer alberto_start_rebuild instead.")]
    public async Task<string> ResetCheckpointAsync(
        [Description("The processor whose checkpoint is deleted.")] string processorId,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default)
    {
        await op.ResetCheckpointAsync(processorId, operatorId, ct);
        return $"Checkpoint for '{processorId}' was reset; it will replay from position 0.";
    }

    [McpServerTool(Name = "alberto_rename_checkpoint", Destructive = true, Idempotent = true)]
    [Description(
        "Atomically copies a checkpoint's position to a new processor ID and removes the old row. " +
        "Use this after renaming a handler class so the renamed processor picks up where the old " +
        "one left off instead of replaying the entire log.")]
    public Task<CheckpointRenameResult> RenameCheckpointAsync(
        [Description("The existing processor ID to rename from.")] string fromProcessorId,
        [Description("The new processor ID to rename to.")] string toProcessorId,
        CancellationToken ct = default) =>
        reader.RenameCheckpointAsync(fromProcessorId, toProcessorId, ct);
}
