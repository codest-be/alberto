using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Internal;

/// <summary>
/// Service for querying admin data from a specific module.
/// </summary>
public interface IAdminQueryService
{
    /// <summary>
    /// The module key this service queries.
    /// </summary>
    string ModuleKey { get; }

    // Processors
    Task<IReadOnlyList<ProcessorStatusDto>> GetProcessorsAsync(CancellationToken ct = default);
    Task SetProcessorActiveAsync(string processorId, bool active, CancellationToken ct = default);

    // Checkpoints
    Task<IReadOnlyList<CheckpointDto>> GetCheckpointsAsync(CancellationToken ct = default);
    Task ResetCheckpointAsync(string processorId, CancellationToken ct = default);
    Task SetCheckpointAsync(string processorId, long position, CancellationToken ct = default);

    // Dead Letters
    Task<PagedResult<DeadLetterDto>> GetDeadLettersAsync(
        string? processorId = null,
        string? eventType = null,
        string? searchTerm = null,
        DateTimeOffset? failedAfter = null,
        DateTimeOffset? failedBefore = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
    Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default);
    Task RemoveDeadLetterAsync(Guid id, CancellationToken ct = default);
    Task ClearDeadLettersAsync(string processorId, CancellationToken ct = default);
    Task<int> GetDeadLetterCountAsync(string? processorId = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDeadLetterEventTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Retries a dead letter event by re-processing it through the processor.
    /// </summary>
    Task<DeadLetterRetryResult> RetryDeadLetterAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Retries all dead letters for a specific processor.
    /// </summary>
    Task<BulkRetryResult> RetryAllDeadLettersAsync(string processorId, CancellationToken ct = default);

    // Checkpoints - Bulk Operations

    /// <summary>
    /// Resets multiple checkpoints at once.
    /// </summary>
    Task<BulkOperationResult> ResetCheckpointsAsync(IReadOnlyList<string> processorIds, CancellationToken ct = default);

    // Projection States
    Task<IReadOnlyList<string>> GetProjectionTypesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetProjectionTenantsAsync(string projectionType, CancellationToken ct = default);
    Task<PagedResult<ProjectionStateDto>> GetProjectionStatesAsync(
        string projectionType,
        string? tenantId = null,
        string? searchTerm = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
    Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId = null,
        CancellationToken ct = default);

    // Projection Rebuilds (Legacy - clears state during rebuild)

    /// <summary>
    /// Starts a projection rebuild by clearing state and resetting the checkpoint.
    /// Note: This is the legacy approach that causes downtime. Consider using StartVersionedRebuildAsync instead.
    /// </summary>
    Task<RebuildStatus> StartRebuildAsync(string processorId, bool clearState = true, CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of a rebuild operation.
    /// </summary>
    Task<RebuildStatus?> GetRebuildStatusAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Cancels an ongoing rebuild operation.
    /// </summary>
    Task CancelRebuildAsync(string processorId, CancellationToken ct = default);

    // Versioned Rebuilds (Zero-downtime)

    /// <summary>
    /// Starts a zero-downtime versioned rebuild.
    /// Creates a new version for the rebuild processor to write to while queries continue reading from the active version.
    /// </summary>
    Task<RebuildStatus> StartVersionedRebuildAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Swaps the active version to the rebuilt version.
    /// Queries will immediately start reading from the new version.
    /// </summary>
    Task<RebuildStatus> SwapRebuildVersionAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Rolls back a versioned rebuild, discarding the rebuilding version data.
    /// </summary>
    Task<RebuildStatus> RollbackRebuildAsync(string processorId, CancellationToken ct = default);

    /// <summary>
    /// Cleans up old version data after a successful swap.
    /// </summary>
    Task CleanupOldVersionAsync(string processorId, int versionToDelete, CancellationToken ct = default);

    // System
    Task<long> GetLastGlobalPositionAsync(CancellationToken ct = default);
    Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct = default);
}
