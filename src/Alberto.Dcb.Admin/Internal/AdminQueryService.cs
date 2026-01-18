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
    private readonly RebuildOrchestrator? _rebuildOrchestrator;
    private readonly IRebuildMetadataStore? _rebuildMetadataStore;

    // In-memory tracking of rebuild operations (legacy)
    private static readonly Dictionary<string, RebuildTracker> _rebuildTrackers = new();

    private sealed class RebuildTracker
    {
        public required string ProcessorId { get; init; }
        public required RebuildState State { get; set; }
        public required long TargetPosition { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public AdminQueryService(
        string moduleKey,
        ICheckpointStore checkpointStore,
        IEventStoreBackend eventStore,
        IAdminDataAccess dataAccess,
        AdminOptions options,
        IDeadLetterStore? deadLetterStore = null,
        PollingConsumer? consumer = null,
        IRebuildMetadataStore? rebuildMetadataStore = null)
    {
        ModuleKey = moduleKey;
        _checkpointStore = checkpointStore;
        _eventStore = eventStore;
        _dataAccess = dataAccess;
        _options = options;
        _deadLetterStore = deadLetterStore;
        _consumer = consumer;
        _rebuildMetadataStore = rebuildMetadataStore;

        if (rebuildMetadataStore is not null)
        {
            _rebuildOrchestrator = new RebuildOrchestrator(
                rebuildMetadataStore,
                checkpointStore,
                eventStore);
        }
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
                    IsRebuilding: processor.IsRebuilding,
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
        string? eventType = null,
        string? searchTerm = null,
        DateTimeOffset? failedAfter = null,
        DateTimeOffset? failedBefore = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        return _dataAccess.ListDeadLettersAsync(processorId, eventType, searchTerm, failedAfter, failedBefore, page, pageSize, ct);
    }

    public Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default)
    {
        return _dataAccess.GetDeadLetterAsync(id, ct);
    }

    public Task<IReadOnlyList<string>> GetDeadLetterEventTypesAsync(CancellationToken ct = default)
    {
        return _dataAccess.GetDeadLetterEventTypesAsync(ct);
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

    public async Task<DeadLetterRetryResult> RetryDeadLetterAsync(Guid id, CancellationToken ct = default)
    {
        if (_deadLetterStore is null)
            return new DeadLetterRetryResult(id, false, "No dead letter store configured.");

        if (_consumer is null)
            return new DeadLetterRetryResult(id, false, "No consumer registered for this module.");

        // Get the dead letter entry
        var deadLetter = await _dataAccess.GetDeadLetterAsync(id, ct);
        if (deadLetter is null)
            return new DeadLetterRetryResult(id, false, "Dead letter not found.");

        // Find the processor
        var processorsField = typeof(PollingConsumer).GetField("_processors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (processorsField?.GetValue(_consumer) is not List<IEventProcessor> processorList)
            return new DeadLetterRetryResult(id, false, "Could not access processor list.");

        var processor = processorList.FirstOrDefault(p => p.ProcessorId == deadLetter.ProcessorId);
        if (processor is null)
            return new DeadLetterRetryResult(id, false, $"Processor '{deadLetter.ProcessorId}' not found.");

        // Get the original event from the event store
        var eventEnvelope = await _dataAccess.GetEventByIdAsync(deadLetter.EventId, ct);
        if (eventEnvelope is null)
            return new DeadLetterRetryResult(id, false, $"Original event '{deadLetter.EventId}' not found in event store.");

        try
        {
            // Re-process the event
            await processor.ProcessEventAsync(eventEnvelope, ct);

            // Success - remove the dead letter
            await _deadLetterStore.RemoveAsync(id, ct);

            return new DeadLetterRetryResult(id, true, null);
        }
        catch (Exception ex)
        {
            return new DeadLetterRetryResult(id, false, $"Retry failed: {ex.Message}");
        }
    }

    public async Task<BulkRetryResult> RetryAllDeadLettersAsync(string processorId, CancellationToken ct = default)
    {
        if (_deadLetterStore is null)
            return new BulkRetryResult(0, 0, 0, []);

        // Get all dead letters for this processor
        var deadLetters = await _dataAccess.ListDeadLettersAsync(processorId, null, null, null, null, 1, 1000, ct);
        var results = new List<DeadLetterRetryResult>();

        foreach (var dl in deadLetters.Items)
        {
            var result = await RetryDeadLetterAsync(dl.Id, ct);
            results.Add(result);
        }

        return new BulkRetryResult(
            TotalAttempted: results.Count,
            SuccessCount: results.Count(r => r.Success),
            FailCount: results.Count(r => !r.Success),
            Results: results);
    }

    #endregion

    #region Checkpoints - Bulk Operations

    public async Task<BulkOperationResult> ResetCheckpointsAsync(IReadOnlyList<string> processorIds, CancellationToken ct = default)
    {
        var results = new List<OperationItemResult>();

        foreach (var processorId in processorIds)
        {
            try
            {
                await _checkpointStore.ResetAsync(processorId, ct);
                results.Add(new OperationItemResult(processorId, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new OperationItemResult(processorId, false, ex.Message));
            }
        }

        return new BulkOperationResult(
            TotalCount: results.Count,
            SuccessCount: results.Count(r => r.Success),
            FailCount: results.Count(r => !r.Success),
            Items: results);
    }

    #endregion

    #region Projection States

    public Task<IReadOnlyList<string>> GetProjectionTypesAsync(CancellationToken ct = default)
    {
        return _dataAccess.ListProjectionTypesAsync(ct);
    }

    public Task<IReadOnlyList<string>> GetProjectionTenantsAsync(string projectionType, CancellationToken ct = default)
    {
        return _dataAccess.GetProjectionTenantsAsync(projectionType, ct);
    }

    public Task<PagedResult<ProjectionStateDto>> GetProjectionStatesAsync(
        string projectionType,
        string? tenantId = null,
        string? searchTerm = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        return _dataAccess.ListProjectionStatesAsync(projectionType, tenantId, searchTerm, updatedAfter, updatedBefore, page, pageSize, ct);
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

    #region Projection Rebuilds

    public async Task<RebuildStatus> StartRebuildAsync(string processorId, bool clearState = true, CancellationToken ct = default)
    {
        if (_consumer is null)
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null, "No consumer registered.");

        // Get the processor to check if already rebuilding
        var processorsField = typeof(PollingConsumer).GetField("_processors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (processorsField?.GetValue(_consumer) is not List<IEventProcessor> processorList)
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null, "Could not access processor list.");

        var processor = processorList.FirstOrDefault(p => p.ProcessorId == processorId);
        if (processor is null)
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null, $"Processor '{processorId}' not found.");

        // Check if processor is already rebuilding
        if (processor.IsRebuilding)
            return await GetRebuildStatusAsync(processorId, ct) ?? new RebuildStatus(processorId, RebuildState.Rebuilding, 0, 0, 0, null, null, "Rebuild already in progress.");

        // Check if tracker says already rebuilding
        if (_rebuildTrackers.ContainsKey(processorId))
        {
            var existing = _rebuildTrackers[processorId];
            if (existing.State == RebuildState.Rebuilding || existing.State == RebuildState.Clearing)
            {
                return await GetRebuildStatusAsync(processorId, ct) ?? new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null, "Rebuild in progress.");
            }
        }

        var targetPosition = await _eventStore.GetLastPositionGlobal(ct);
        var startedAt = DateTimeOffset.UtcNow;

        // Create tracker
        var tracker = new RebuildTracker
        {
            ProcessorId = processorId,
            State = RebuildState.Clearing,
            TargetPosition = targetPosition,
            StartedAt = startedAt
        };
        _rebuildTrackers[processorId] = tracker;

        try
        {
            // Step 1: Clear projection state if requested
            if (clearState)
            {
                await _dataAccess.ClearProjectionStatesAsync(processorId, ct);
            }

            // Step 2: Trigger rebuild via the consumer (resets checkpoint and starts independent task)
            tracker.State = RebuildState.Rebuilding;
            var triggered = await _consumer.TriggerRebuildAsync(processorId, ct);

            if (!triggered)
            {
                tracker.State = RebuildState.Failed;
                tracker.ErrorMessage = "Failed to trigger rebuild - processor may already be rebuilding.";
                tracker.CompletedAt = DateTimeOffset.UtcNow;
                return new RebuildStatus(processorId, RebuildState.Failed, 0, targetPosition, 0, startedAt, tracker.CompletedAt, tracker.ErrorMessage);
            }

            return await GetRebuildStatusAsync(processorId, ct) ?? new RebuildStatus(processorId, RebuildState.Rebuilding, 0, targetPosition, 0, startedAt, null, null);
        }
        catch (Exception ex)
        {
            tracker.State = RebuildState.Failed;
            tracker.ErrorMessage = ex.Message;
            tracker.CompletedAt = DateTimeOffset.UtcNow;
            return new RebuildStatus(processorId, RebuildState.Failed, 0, targetPosition, 0, startedAt, tracker.CompletedAt, ex.Message);
        }
    }

    public async Task<RebuildStatus?> GetRebuildStatusAsync(string processorId, CancellationToken ct = default)
    {
        if (!_rebuildTrackers.TryGetValue(processorId, out var tracker))
            return null;

        // Get current checkpoint position
        var checkpoints = await _dataAccess.ListCheckpointsAsync(ct);
        var checkpoint = checkpoints.FirstOrDefault(c => c.ProcessorId == processorId);
        var currentPosition = checkpoint?.LastPosition ?? 0;

        // Calculate progress
        var progressPercent = tracker.TargetPosition > 0
            ? Math.Min(100, (double)currentPosition / tracker.TargetPosition * 100)
            : 100;

        // Check if completed - either by position or by processor no longer rebuilding
        if (tracker.State == RebuildState.Rebuilding)
        {
            // Check if the processor's IsRebuilding flag is now false (meaning it caught up)
            var processorsField = typeof(PollingConsumer).GetField("_processors",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (_consumer is not null && processorsField?.GetValue(_consumer) is List<IEventProcessor> processorList)
            {
                var processor = processorList.FirstOrDefault(p => p.ProcessorId == processorId);
                if (processor is not null && !processor.IsRebuilding)
                {
                    // Processor finished rebuilding (caught up within threshold)
                    tracker.State = RebuildState.Completed;
                    tracker.CompletedAt = DateTimeOffset.UtcNow;
                }
            }

            // Also check by position
            if (tracker.State == RebuildState.Rebuilding && currentPosition >= tracker.TargetPosition)
            {
                tracker.State = RebuildState.Completed;
                tracker.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return new RebuildStatus(
            tracker.ProcessorId,
            tracker.State,
            currentPosition,
            tracker.TargetPosition,
            progressPercent,
            tracker.StartedAt,
            tracker.CompletedAt,
            tracker.ErrorMessage);
    }

    public Task CancelRebuildAsync(string processorId, CancellationToken ct = default)
    {
        if (_rebuildTrackers.TryGetValue(processorId, out var tracker))
        {
            if (tracker.State == RebuildState.Rebuilding || tracker.State == RebuildState.Clearing)
            {
                tracker.State = RebuildState.Cancelled;
                tracker.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Versioned Rebuilds

    public async Task<RebuildStatus> StartVersionedRebuildAsync(string processorId, CancellationToken ct = default)
    {
        if (_rebuildOrchestrator is null || _rebuildMetadataStore is null)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null,
                "Versioned rebuilds not configured. Ensure IRebuildMetadataStore is registered.");
        }

        try
        {
            var handle = await _rebuildOrchestrator.StartAsync(processorId, ct);

            // TODO: Start rebuild processor that writes to the new version
            // For now, we'll need the consumer to support this (future enhancement)

            return new RebuildStatus(
                processorId,
                RebuildState.Rebuilding,
                0,
                handle.TargetPosition,
                0,
                handle.StartedAt,
                null,
                null,
                handle.ActiveVersion,
                handle.RebuildingVersion);
        }
        catch (Exception ex)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ex.Message);
        }
    }

    public async Task<RebuildStatus> SwapRebuildVersionAsync(string processorId, CancellationToken ct = default)
    {
        if (_rebuildOrchestrator is null || _rebuildMetadataStore is null)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null,
                "Versioned rebuilds not configured. Ensure IRebuildMetadataStore is registered.");
        }

        try
        {
            await _rebuildOrchestrator.SwapAsync(processorId, ct);
            var status = await _rebuildOrchestrator.GetStatusAsync(processorId, ct);

            if (status is null)
            {
                return new RebuildStatus(processorId, RebuildState.Swapped, 0, 0, 100,
                    null, DateTimeOffset.UtcNow, null);
            }

            return new RebuildStatus(
                processorId,
                RebuildState.Swapped,
                status.CurrentPosition,
                status.TargetPosition,
                status.ProgressPercent,
                status.StartedAt,
                status.CompletedAt,
                null,
                status.ActiveVersion,
                status.RebuildingVersion);
        }
        catch (Exception ex)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0,
                null, DateTimeOffset.UtcNow, ex.Message);
        }
    }

    public async Task<RebuildStatus> RollbackRebuildAsync(string processorId, CancellationToken ct = default)
    {
        if (_rebuildOrchestrator is null || _rebuildMetadataStore is null)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0, null, null,
                "Versioned rebuilds not configured. Ensure IRebuildMetadataStore is registered.");
        }

        try
        {
            await _rebuildOrchestrator.RollbackAsync(processorId, ct);
            var status = await _rebuildOrchestrator.GetStatusAsync(processorId, ct);

            if (status is null)
            {
                return new RebuildStatus(processorId, RebuildState.RolledBack, 0, 0, 0,
                    null, DateTimeOffset.UtcNow, null);
            }

            return new RebuildStatus(
                processorId,
                RebuildState.RolledBack,
                status.CurrentPosition,
                status.TargetPosition,
                status.ProgressPercent,
                status.StartedAt,
                status.CompletedAt,
                null,
                status.ActiveVersion,
                status.RebuildingVersion);
        }
        catch (Exception ex)
        {
            return new RebuildStatus(processorId, RebuildState.Failed, 0, 0, 0,
                null, DateTimeOffset.UtcNow, ex.Message);
        }
    }

    public async Task CleanupOldVersionAsync(string processorId, int versionToDelete, CancellationToken ct = default)
    {
        if (_rebuildOrchestrator is null || _rebuildMetadataStore is null)
        {
            throw new InvalidOperationException(
                "Versioned rebuilds not configured. Ensure IRebuildMetadataStore is registered.");
        }

        await _rebuildOrchestrator.CleanupVersionAsync(processorId, versionToDelete, ct);

        // Perform actual data deletion through the data access layer
        await _dataAccess.DeleteProjectionVersionAsync(processorId, versionToDelete, ct);
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
