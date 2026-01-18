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
    /// Lists projection states with pagination and filtering.
    /// </summary>
    Task<PagedResult<ProjectionStateDto>> ListProjectionStatesAsync(
        string projectionType,
        string? tenantId,
        string? searchTerm,
        DateTimeOffset? updatedAfter,
        DateTimeOffset? updatedBefore,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all distinct tenant IDs from projection states for filtering.
    /// </summary>
    Task<IReadOnlyList<string>> GetProjectionTenantsAsync(string projectionType, CancellationToken ct = default);

    /// <summary>
    /// Gets a single projection state.
    /// </summary>
    Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists dead letters with pagination and filtering.
    /// </summary>
    Task<PagedResult<DeadLetterDto>> ListDeadLettersAsync(
        string? processorId,
        string? eventType,
        string? searchTerm,
        DateTimeOffset? failedAfter,
        DateTimeOffset? failedBefore,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all distinct event types from dead letters for filtering.
    /// </summary>
    Task<IReadOnlyList<string>> GetDeadLetterEventTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a single dead letter by ID.
    /// </summary>
    Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets total dead letter count.
    /// </summary>
    Task<int> GetDeadLetterCountAsync(string? processorId, CancellationToken ct = default);

    /// <summary>
    /// Gets an event by its ID for retry purposes.
    /// </summary>
    Task<IEventEnvelope?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>
    /// Clears all projection states for a specific projection type.
    /// </summary>
    Task ClearProjectionStatesAsync(string projectionType, CancellationToken ct = default);

    /// <summary>
    /// Deletes all projection states for a specific projection type and version.
    /// Used during cleanup of old versions after a versioned rebuild.
    /// </summary>
    Task DeleteProjectionVersionAsync(string projectionType, int version, CancellationToken ct = default);
}
