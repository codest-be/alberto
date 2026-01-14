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
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
    Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default);
    Task RemoveDeadLetterAsync(Guid id, CancellationToken ct = default);
    Task ClearDeadLettersAsync(string processorId, CancellationToken ct = default);
    Task<int> GetDeadLetterCountAsync(string? processorId = null, CancellationToken ct = default);

    // Projection States
    Task<IReadOnlyList<string>> GetProjectionTypesAsync(CancellationToken ct = default);
    Task<PagedResult<ProjectionStateDto>> GetProjectionStatesAsync(
        string projectionType,
        string? tenantId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
    Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId = null,
        CancellationToken ct = default);

    // System
    Task<long> GetLastGlobalPositionAsync(CancellationToken ct = default);
    Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct = default);
}
