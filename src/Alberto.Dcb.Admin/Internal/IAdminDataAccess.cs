using Alberto.Dcb.Admin.Api.Models;

namespace Alberto.Dcb.Admin.Internal;

/// <summary>
/// Database access interface for admin queries that aren't supported by standard interfaces.
/// </summary>
public interface IAdminDataAccess
{
    /// <summary>
    /// Lists all checkpoints in the system.
    /// </summary>
    Task<IReadOnlyList<CheckpointDto>> ListCheckpointsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all distinct projection types.
    /// </summary>
    Task<IReadOnlyList<string>> ListProjectionTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists projection states with pagination.
    /// </summary>
    Task<PagedResult<ProjectionStateDto>> ListProjectionStatesAsync(
        string projectionType,
        string? tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a single projection state.
    /// </summary>
    Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists dead letters with pagination.
    /// </summary>
    Task<PagedResult<DeadLetterDto>> ListDeadLettersAsync(
        string? processorId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a single dead letter by ID.
    /// </summary>
    Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets total dead letter count.
    /// </summary>
    Task<int> GetDeadLetterCountAsync(string? processorId, CancellationToken ct = default);
}
