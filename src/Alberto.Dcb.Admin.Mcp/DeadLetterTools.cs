using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Alberto.Dcb.Admin.Mcp;

/// <summary>
/// Dead-letter inspection and the three ways to clear one: dismiss (drop it),
/// clear-for-processor (drop that processor's), and retry-by-rewind (replay it).
/// </summary>
[McpServerToolType]
public sealed class DeadLetterTools(IAdminReader reader, IAdminOperator op)
{
    [McpServerTool(Name = "alberto_list_dead_letters", ReadOnly = true)]
    [Description(
        "Events that failed processing and were parked, newest first. Each entry carries the " +
        "processor that failed, the event type and global position, and the error message. " +
        "This is the first place to look when a projection has stopped producing correct results.")]
    public Task<List<DeadLetterInfo>> ListDeadLettersAsync(
        [Description("Only dead letters for this processor. Omit for all processors.")] string? processorId = null,
        [Description("Only dead letters for this event type. Omit for all types.")] string? type = null,
        [Description("Only dead letters belonging to this tenant. Omit for all tenants. Passing a tenant on a single-tenant store is an error.")] string? tenant = null,
        [Description("Maximum entries to return.")] int limit = 20,
        CancellationToken ct = default) =>
        reader.GetDeadLettersAsync(processorId, type, tenant, limit, ct);

    [McpServerTool(Name = "alberto_dead_letter_count", ReadOnly = true)]
    [Description("Total number of parked dead letters across every processor. Zero means a clean store.")]
    public Task<int> CountDeadLettersAsync(CancellationToken ct = default) =>
        reader.CountAllDeadLettersAsync(ct);

    [McpServerTool(Name = "alberto_clear_all_dead_letters", Destructive = true, Idempotent = true)]
    [Description(
        "DESTRUCTIVE and IRREVERSIBLE. Permanently deletes every dead letter across all " +
        "processors. The parked events are NOT reprocessed — they are dropped, and the " +
        "projections that failed on them stay wrong. Only do this once you have established the " +
        "entries are genuinely not worth replaying. To replay them instead, use " +
        "alberto_retry_by_rewind. Returns the number of rows deleted.")]
    public Task<int> ClearAllDeadLettersAsync(
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.ClearAllDeadLettersAsync(operatorId, ct);

    [McpServerTool(Name = "alberto_clear_dead_letters_for_processor", Destructive = true, Idempotent = true)]
    [Description(
        "DESTRUCTIVE and IRREVERSIBLE. Permanently deletes one processor's dead letters without " +
        "reprocessing them. Returns the number of rows deleted. To replay them instead, use " +
        "alberto_retry_by_rewind.")]
    public Task<int> ClearDeadLettersForProcessorAsync(
        [Description("The processor whose dead letters are deleted.")] string processorId,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.ClearDeadLettersForProcessorAsync(processorId, operatorId, ct);

    [McpServerTool(Name = "alberto_retry_by_rewind", Destructive = true)]
    [Description(
        "DESTRUCTIVE. Rewinds the processor's checkpoint to just before its earliest dead letter " +
        "and clears its dead letters, so the processor replays from that point and re-attempts the " +
        "failed events. Every event after the rewind position is re-consumed, so the handler must " +
        "be idempotent. Use this — not clear — when the failures were caused by a bug you have " +
        "since fixed. RewindPosition is null when the processor had no dead letters.")]
    public Task<RetryByRewindResult> RetryByRewindAsync(
        [Description("The processor to rewind and retry.")] string processorId,
        [Description("Who is performing this action; recorded in the audit trail.")] string operatorId = DefaultOperator.Id,
        CancellationToken ct = default) =>
        op.RetryByRewindAsync(processorId, operatorId, ct);
}
