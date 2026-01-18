namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Orchestrates zero-downtime projection rebuilds using version isolation.
/// During rebuild, two versions of projection data coexist:
/// - Active version (queries read from this)
/// - Rebuilding version (new data written here)
/// After rebuild completes, versions are atomically swapped.
/// </summary>
public sealed class RebuildOrchestrator(
    IRebuildMetadataStore metadataStore,
    ICheckpointStore checkpointStore,
    IEventStoreBackend eventStore)
{
    private readonly IRebuildMetadataStore _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
    private readonly ICheckpointStore _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    private readonly IEventStoreBackend _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));

    /// <summary>
    /// Starts a versioned rebuild for a projection.
    /// Creates a new version and prepares for the rebuild processor to start writing to it.
    /// </summary>
    /// <param name="projectionType">The projection type (processor ID) to rebuild.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Handle containing version info for the rebuild processor.</returns>
    public async Task<RebuildHandle> StartAsync(string projectionType, CancellationToken ct = default)
    {
        var existingMeta = await _metadataStore.GetAsync(projectionType, ct);

        // Check if already rebuilding
        if (existingMeta?.RebuildStatus == RebuildOperationStatus.Rebuilding)
        {
            throw new InvalidOperationException(
                $"Projection '{projectionType}' is already being rebuilt. " +
                $"Swap or rollback the current rebuild before starting a new one.");
        }

        var activeVersion = existingMeta?.ActiveVersion ?? 1;
        var newVersion = activeVersion + 1;
        var targetPosition = await _eventStore.GetLastPositionGlobal(ct);

        var metadata = new RebuildMetadata
        {
            ProjectionType = projectionType,
            ActiveVersion = activeVersion,
            RebuildingVersion = newVersion,
            RebuildStatus = RebuildOperationStatus.Rebuilding,
            RebuildStartedAt = DateTimeOffset.UtcNow,
            RebuildTargetPosition = targetPosition,
            RebuildCompletedAt = null
        };

        await _metadataStore.SaveAsync(metadata, ct);

        return new RebuildHandle
        {
            ProjectionType = projectionType,
            ActiveVersion = activeVersion,
            RebuildingVersion = newVersion,
            TargetPosition = targetPosition,
            StartedAt = metadata.RebuildStartedAt!.Value
        };
    }

    /// <summary>
    /// Marks the rebuild as ready to swap (caught up to target position).
    /// The rebuild processor should call this when it has caught up.
    /// </summary>
    public async Task MarkReadyToSwapAsync(string projectionType, CancellationToken ct = default)
    {
        var metadata = await _metadataStore.GetAsync(projectionType, ct);
        if (metadata is null)
        {
            throw new InvalidOperationException($"No rebuild metadata found for '{projectionType}'.");
        }

        if (metadata.RebuildStatus != RebuildOperationStatus.Rebuilding)
        {
            throw new InvalidOperationException(
                $"Cannot mark '{projectionType}' as ready to swap: status is {metadata.RebuildStatus}.");
        }

        var updated = metadata with
        {
            RebuildStatus = RebuildOperationStatus.ReadyToSwap,
            RebuildCompletedAt = DateTimeOffset.UtcNow
        };

        await _metadataStore.SaveAsync(updated, ct);
    }

    /// <summary>
    /// Gets the current rebuild status for a projection.
    /// </summary>
    public async Task<VersionedRebuildStatus?> GetStatusAsync(string projectionType, CancellationToken ct = default)
    {
        var metadata = await _metadataStore.GetAsync(projectionType, ct);
        if (metadata is null)
            return null;

        // Get current checkpoint position for the rebuilding version if applicable
        long currentPosition = 0;
        if (metadata.RebuildingVersion.HasValue &&
            (metadata.RebuildStatus == RebuildOperationStatus.Rebuilding ||
             metadata.RebuildStatus == RebuildOperationStatus.ReadyToSwap))
        {
            var rebuildCheckpointId = GetRebuildCheckpointId(projectionType, metadata.RebuildingVersion.Value);
            currentPosition = await _checkpointStore.GetAsync(rebuildCheckpointId, ct) ?? 0;
        }

        var progressPercent = metadata.RebuildTargetPosition > 0
            ? Math.Min(100, (double)currentPosition / metadata.RebuildTargetPosition.Value * 100)
            : 100;

        return new VersionedRebuildStatus
        {
            ProjectionType = projectionType,
            ActiveVersion = metadata.ActiveVersion,
            RebuildingVersion = metadata.RebuildingVersion,
            Status = metadata.RebuildStatus,
            CurrentPosition = currentPosition,
            TargetPosition = metadata.RebuildTargetPosition ?? 0,
            ProgressPercent = progressPercent,
            StartedAt = metadata.RebuildStartedAt,
            CompletedAt = metadata.RebuildCompletedAt
        };
    }

    /// <summary>
    /// Atomically swaps the active version to the rebuilt version.
    /// After this, queries will read from the new version.
    /// The old version data is retained until CleanupAsync is called.
    /// </summary>
    public async Task SwapAsync(string projectionType, CancellationToken ct = default)
    {
        var metadata = await _metadataStore.GetAsync(projectionType, ct);
        if (metadata is null)
        {
            throw new InvalidOperationException($"No rebuild metadata found for '{projectionType}'.");
        }

        if (metadata.RebuildStatus != RebuildOperationStatus.ReadyToSwap &&
            metadata.RebuildStatus != RebuildOperationStatus.Rebuilding)
        {
            throw new InvalidOperationException(
                $"Cannot swap '{projectionType}': status is {metadata.RebuildStatus}. " +
                "Rebuild must be in 'Rebuilding' or 'ReadyToSwap' status.");
        }

        if (!metadata.RebuildingVersion.HasValue)
        {
            throw new InvalidOperationException(
                $"Cannot swap '{projectionType}': no rebuilding version found.");
        }

        var newActiveVersion = metadata.RebuildingVersion.Value;

        // Atomic swap: update active version
        var updated = metadata with
        {
            ActiveVersion = newActiveVersion,
            RebuildingVersion = null,
            RebuildStatus = RebuildOperationStatus.Swapped,
            RebuildCompletedAt = DateTimeOffset.UtcNow
        };

        await _metadataStore.SaveAsync(updated, ct);

        // Copy the rebuild checkpoint to the main checkpoint
        var rebuildCheckpointId = GetRebuildCheckpointId(projectionType, newActiveVersion);
        var rebuildPosition = await _checkpointStore.GetAsync(rebuildCheckpointId, ct) ?? 0;
        await _checkpointStore.SaveAsync(projectionType, rebuildPosition, ct);
    }

    /// <summary>
    /// Rolls back a rebuild, discarding the rebuilding version data.
    /// </summary>
    public async Task RollbackAsync(string projectionType, CancellationToken ct = default)
    {
        var metadata = await _metadataStore.GetAsync(projectionType, ct);
        if (metadata is null)
        {
            throw new InvalidOperationException($"No rebuild metadata found for '{projectionType}'.");
        }

        if (metadata.RebuildStatus != RebuildOperationStatus.Rebuilding &&
            metadata.RebuildStatus != RebuildOperationStatus.ReadyToSwap)
        {
            throw new InvalidOperationException(
                $"Cannot rollback '{projectionType}': status is {metadata.RebuildStatus}. " +
                "Can only rollback when status is 'Rebuilding' or 'ReadyToSwap'.");
        }

        // Update status to rolled back (keep the metadata for history)
        var updated = metadata with
        {
            RebuildingVersion = null,
            RebuildStatus = RebuildOperationStatus.RolledBack,
            RebuildCompletedAt = DateTimeOffset.UtcNow
        };

        await _metadataStore.SaveAsync(updated, ct);

        // Clean up the rebuild checkpoint
        if (metadata.RebuildingVersion.HasValue)
        {
            var rebuildCheckpointId = GetRebuildCheckpointId(projectionType, metadata.RebuildingVersion.Value);
            await _checkpointStore.ResetAsync(rebuildCheckpointId, ct);
        }
    }

    /// <summary>
    /// Cleans up old version data after a successful swap.
    /// This should be called after verifying the new version is working correctly.
    /// </summary>
    /// <param name="projectionType">The projection type to clean up.</param>
    /// <param name="versionToDelete">The old version number to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CleanupVersionAsync(string projectionType, int versionToDelete, CancellationToken ct = default)
    {
        var metadata = await _metadataStore.GetAsync(projectionType, ct);
        if (metadata is null)
        {
            throw new InvalidOperationException($"No rebuild metadata found for '{projectionType}'.");
        }

        // Prevent deleting the active version
        if (versionToDelete == metadata.ActiveVersion)
        {
            throw new InvalidOperationException(
                $"Cannot delete version {versionToDelete} for '{projectionType}': it is the active version.");
        }

        // Note: The actual data deletion is done by the caller (PostgresRebuildMetadataStore.DeleteVersionDataAsync)
        // This method just validates the operation is safe
    }

    /// <summary>
    /// Gets the checkpoint ID for a rebuild processor.
    /// </summary>
    public static string GetRebuildCheckpointId(string projectionType, int version)
    {
        return $"{projectionType}:rebuild:v{version}";
    }
}

/// <summary>
/// Handle returned when starting a rebuild, containing version info.
/// </summary>
public sealed record RebuildHandle
{
    public required string ProjectionType { get; init; }
    public required int ActiveVersion { get; init; }
    public required int RebuildingVersion { get; init; }
    public required long TargetPosition { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>
/// Status of a versioned rebuild operation.
/// </summary>
public sealed record VersionedRebuildStatus
{
    public required string ProjectionType { get; init; }
    public required int ActiveVersion { get; init; }
    public int? RebuildingVersion { get; init; }
    public RebuildOperationStatus? Status { get; init; }
    public long CurrentPosition { get; init; }
    public long TargetPosition { get; init; }
    public double ProgressPercent { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
