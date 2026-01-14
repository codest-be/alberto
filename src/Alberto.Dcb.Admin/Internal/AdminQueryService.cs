using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Admin.Internal;

/// <summary>
/// Default implementation of <see cref="IAdminQueryService"/>.
/// </summary>
internal sealed class AdminQueryService : IAdminQueryService
{
    private readonly ICheckpointStore _checkpointStore;
    private readonly IDeadLetterStore? _deadLetterStore;
    private readonly IEventStoreBackend _eventStore;
    private readonly IAdminDataAccess _dataAccess;
    private readonly PollingConsumer? _consumer;
    private readonly AdminOptions _options;

    public AdminQueryService(
        string moduleKey,
        ICheckpointStore checkpointStore,
        IEventStoreBackend eventStore,
        IAdminDataAccess dataAccess,
        AdminOptions options,
        IDeadLetterStore? deadLetterStore = null,
        PollingConsumer? consumer = null)
    {
        ModuleKey = moduleKey;
        _checkpointStore = checkpointStore;
        _eventStore = eventStore;
        _dataAccess = dataAccess;
        _options = options;
        _deadLetterStore = deadLetterStore;
        _consumer = consumer;
    }

    public string ModuleKey { get; }

    #region Processors

    public async Task<IReadOnlyList<ProcessorStatusDto>> GetProcessorsAsync(CancellationToken ct = default)
    {
        if (_consumer is null)
            return [];

        var globalPosition = await _eventStore.GetLastPositionGlobal(ct);
        var checkpoints = await _dataAccess.ListCheckpointsAsync(ct);
        var checkpointMap = checkpoints.ToDictionary(c => c.ProcessorId);

        var processors = new List<ProcessorStatusDto>();

        // Use reflection to access internal processors list
        var processorsField = typeof(PollingConsumer).GetField("_processors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (processorsField?.GetValue(_consumer) is List<IEventProcessor> processorList)
        {
            foreach (var processor in processorList)
            {
                var checkpoint = checkpointMap.GetValueOrDefault(processor.ProcessorId);
                var lastPosition = checkpoint?.LastPosition ?? 0;
                var deadLetterCount = _deadLetterStore is not null
                    ? await _deadLetterStore.CountAsync(processor.ProcessorId, ct)
                    : 0;

                processors.Add(new ProcessorStatusDto(
                    ProcessorId: processor.ProcessorId,
                    IsActive: processor.IsActive,
                    LastPosition: checkpoint?.LastPosition,
                    GlobalPosition: globalPosition,
                    Lag: globalPosition - lastPosition,
                    LastUpdated: checkpoint?.UpdatedAt,
                    HandledEventTypes: processor.HandledEventTypes,
                    DeadLetterCount: deadLetterCount));
            }
        }

        return processors;
    }

    public Task SetProcessorActiveAsync(string processorId, bool active, CancellationToken ct = default)
    {
        if (_consumer is null)
            throw new InvalidOperationException("No consumer registered for this module.");

        var processorsField = typeof(PollingConsumer).GetField("_processors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (processorsField?.GetValue(_consumer) is List<IEventProcessor> processorList)
        {
            var processor = processorList.FirstOrDefault(p => p.ProcessorId == processorId);
            if (processor is not null)
            {
                processor.IsActive = active;
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Checkpoints

    public Task<IReadOnlyList<CheckpointDto>> GetCheckpointsAsync(CancellationToken ct = default)
    {
        return _dataAccess.ListCheckpointsAsync(ct);
    }

    public Task ResetCheckpointAsync(string processorId, CancellationToken ct = default)
    {
        return _checkpointStore.ResetAsync(processorId, ct);
    }

    public Task SetCheckpointAsync(string processorId, long position, CancellationToken ct = default)
    {
        return _checkpointStore.SaveAsync(processorId, position, ct);
    }

    #endregion

    #region Dead Letters

    public Task<PagedResult<DeadLetterDto>> GetDeadLettersAsync(
        string? processorId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        return _dataAccess.ListDeadLettersAsync(processorId, page, pageSize, ct);
    }

    public Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default)
    {
        return _dataAccess.GetDeadLetterAsync(id, ct);
    }

    public async Task RemoveDeadLetterAsync(Guid id, CancellationToken ct = default)
    {
        if (_deadLetterStore is null)
            throw new InvalidOperationException("No dead letter store configured.");

        await _deadLetterStore.RemoveAsync(id, ct);
    }

    public async Task ClearDeadLettersAsync(string processorId, CancellationToken ct = default)
    {
        if (_deadLetterStore is null)
            throw new InvalidOperationException("No dead letter store configured.");

        await _deadLetterStore.ClearAsync(processorId, ct);
    }

    public Task<int> GetDeadLetterCountAsync(string? processorId = null, CancellationToken ct = default)
    {
        return _dataAccess.GetDeadLetterCountAsync(processorId, ct);
    }

    #endregion

    #region Projection States

    public Task<IReadOnlyList<string>> GetProjectionTypesAsync(CancellationToken ct = default)
    {
        return _dataAccess.ListProjectionTypesAsync(ct);
    }

    public Task<PagedResult<ProjectionStateDto>> GetProjectionStatesAsync(
        string projectionType,
        string? tenantId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        return _dataAccess.ListProjectionStatesAsync(projectionType, tenantId, page, pageSize, ct);
    }

    public Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        return _dataAccess.GetProjectionStateAsync(projectionType, documentId, tenantId, ct);
    }

    #endregion

    #region System

    public Task<long> GetLastGlobalPositionAsync(CancellationToken ct = default)
    {
        return _eventStore.GetLastPositionGlobal(ct);
    }

    public async Task<SystemInfoDto> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var globalPosition = await _eventStore.GetLastPositionGlobal(ct);
        var processors = await GetProcessorsAsync(ct);
        var deadLetterCount = await GetDeadLetterCountAsync(ct: ct);

        return new SystemInfoDto(
            ModuleKey: ModuleKey,
            GlobalPosition: globalPosition,
            ProcessorCount: processors.Count,
            DeadLetterCount: deadLetterCount,
            ReadOnlyMode: _options.ReadOnly);
    }

    #endregion
}
