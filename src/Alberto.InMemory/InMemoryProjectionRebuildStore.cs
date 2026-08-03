using Alberto.Subscriptions;

namespace Alberto.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IProjectionRebuildStore"/> and the internal
/// coordinator store. Thread-safe for concurrent access.
/// </summary>
/// <remarks>
/// <para>
/// This adapter exists for testing the rebuild protocol itself — conformance specs,
/// unit tests, and fake coordinator setups — not for enabling <c>.WithRebuilds()</c>
/// on in-memory modules. The rebuild protocol spans process restarts: a shadow loop
/// resumes via <see cref="ProjectionRebuildState.IsRebuildInFlight"/> after the coordinator
/// is restarted, and the reclaim sweep runs only after <c>ReclaimGracePeriod</c> elapses.
/// State that is discarded when the process exits makes that protocol meaningless, so
/// <c>ALB0023</c> stays — see <see cref="InMemoryBackendDescriptor"/>.
/// </para>
/// <para>
/// <see cref="IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync"/> is a no-op.
/// In-memory projection state lives in <c>InMemoryStateStore</c> instances, not in rows
/// this store controls. The interface contract already says that reclaiming a version with
/// no rows is safe.
/// </para>
/// </remarks>
public sealed class InMemoryProjectionRebuildStore :
    IProjectionRebuildStore,
    IProjectionRebuildCoordinatorStore
{
    private readonly object _lock = new();

    // Keyed by processorId, matching the Postgres adapter's primary key.
    private readonly Dictionary<string, ProjectionRebuildState> _states = new();

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> GetAsync(
        string processorId, string projectionType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);

        lock (_lock)
        {
            if (_states.TryGetValue(processorId, out var existing))
                return Task.FromResult(existing);

            // No row yet: report as idle at version 1, matching the Postgres adapter.
            return Task.FromResult(new ProjectionRebuildState(
                processorId, projectionType, ActiveVersion: 1, RebuildingVersion: null,
                RebuildStatus.Idle, StartedAt: null, TargetPosition: null, CompletedAt: null));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ProjectionRebuildState>>(
                _states.Values.OrderBy(s => s.ProcessorId).ToList());
        }
    }

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> StartAsync(
        string processorId, string projectionType, long targetPosition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        ArgumentOutOfRangeException.ThrowIfNegative(targetPosition);

        lock (_lock)
        {
            if (_states.TryGetValue(processorId, out var current))
            {
                // Mirror the Postgres adapter's conditional upsert: a row already in
                // 'rebuilding' or 'ready' is blocked, not overwritten.
                if (current.IsRebuildInFlight)
                    throw new RebuildStateException(
                        $"Cannot start a rebuild for processor '{processorId}': one is already in flight. " +
                        "Promote it, or abort it first.");

                // Allocate the next version from the high-water mark. Using
                // last_allocated_version rather than active_version + 1 prevents a shadow
                // loop that has not yet noticed an abort from seeding the next replay with
                // its own leftovers — the same reason the Postgres adapter tracks this
                // separately (see migration 015).
                var next = current.LastAllocatedVersion + 1;
                var updated = current with
                {
                    ProjectionType = projectionType,
                    RebuildingVersion = next,
                    LastAllocatedVersion = next,
                    Status = RebuildStatus.Rebuilding,
                    StartedAt = DateTimeOffset.UtcNow,
                    TargetPosition = targetPosition,
                    CompletedAt = null,
                    RequestedAction = null,
                };
                _states[processorId] = updated;
                return Task.FromResult(updated);
            }
            else
            {
                // First ever rebuild for this processor: active version stays 1,
                // rebuilding version is 2 (allocating from 1+1 = 2).
                var state = new ProjectionRebuildState(
                    processorId, projectionType, ActiveVersion: 1, RebuildingVersion: 2,
                    RebuildStatus.Rebuilding,
                    StartedAt: DateTimeOffset.UtcNow,
                    TargetPosition: targetPosition,
                    CompletedAt: null,
                    LastAllocatedVersion: 2,
                    RequestedAction: null);
                _states[processorId] = state;
                return Task.FromResult(state);
            }
        }
    }

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> RequestPromotionAsync(
        string processorId, bool force = false, CancellationToken ct = default) =>
        RequestActionAsync(
            processorId,
            force ? RebuildOperatorAction.ForcePromote : RebuildOperatorAction.Promote,
            ct);

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> RequestAbortAsync(
        string processorId, CancellationToken ct = default) =>
        RequestActionAsync(processorId, RebuildOperatorAction.Abort, ct);

    private Task<ProjectionRebuildState> RequestActionAsync(
        string processorId,
        RebuildOperatorAction action,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        lock (_lock)
        {
            if (!_states.TryGetValue(processorId, out var current) || !current.IsRebuildInFlight)
                throw new RebuildStateException(
                    $"Cannot request {action} for processor '{processorId}': no rebuild is in flight.");

            var updated = current with { RequestedAction = action };
            _states[processorId] = updated;
            return Task.FromResult(updated);
        }
    }

    /// <inheritdoc/>
    Task<ProjectionRebuildState> IProjectionRebuildCoordinatorStore.MarkReadyAsync(
        string processorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        lock (_lock)
        {
            if (!_states.TryGetValue(processorId, out var current)
                || current.Status is not RebuildStatus.Rebuilding)
                throw new RebuildStateException(
                    $"Cannot mark processor '{processorId}' ready: no rebuild is in flight.");

            var updated = current with { Status = RebuildStatus.Ready };
            _states[processorId] = updated;
            return Task.FromResult(updated);
        }
    }

    /// <inheritdoc/>
    Task<RebuildOutcome> IProjectionRebuildCoordinatorStore.CompletePromotionAsync(
        string processorId, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        lock (_lock)
        {
            if (!_states.TryGetValue(processorId, out var current))
                throw new RebuildStateException(
                    $"Cannot promote processor '{processorId}': no rebuild has ever been started for it.");

            if (!current.IsRebuildInFlight)
                throw new RebuildStateException(
                    $"Cannot promote processor '{processorId}': no rebuild is in flight " +
                    $"(status is {current.Status}).");

            if (current.Status is RebuildStatus.Rebuilding && !force)
                throw new RebuildStateException(
                    $"Cannot promote processor '{processorId}': the rebuild has not finished " +
                    $"replaying (target position {current.TargetPosition}). Wait for it to reach " +
                    "Ready, or use an early-promotion request.");

            var discardedVersion = current.ActiveVersion;
            var updated = current with
            {
                ActiveVersion = current.RebuildingVersion!.Value,
                RebuildingVersion = null,
                Status = RebuildStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                RequestedAction = null,
            };
            _states[processorId] = updated;

            // The superseded version's rows are left intact, as in the Postgres adapter:
            // a reader that resolved the old version number before the flip must not find
            // empty state. DiscardStateVersionAsync reclaims them after the grace period.
            return Task.FromResult(new RebuildOutcome(updated, discardedVersion));
        }
    }

    /// <inheritdoc/>
    Task<RebuildOutcome> IProjectionRebuildCoordinatorStore.CompleteAbortAsync(
        string processorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        lock (_lock)
        {
            if (!_states.TryGetValue(processorId, out var current) || !current.IsRebuildInFlight)
                throw new RebuildStateException(
                    $"Cannot abort a rebuild for processor '{processorId}': none is in flight.");

            var abandonedVersion = current.RebuildingVersion!.Value;
            var updated = current with
            {
                RebuildingVersion = null,
                Status = RebuildStatus.Aborted,
                CompletedAt = DateTimeOffset.UtcNow,
                RequestedAction = null,
            };
            _states[processorId] = updated;

            // The abandoned version's rows outlive the transition, for the same reason as
            // CompletePromotionAsync, plus a second one: the shadow loop only learns of the
            // abort on its next poll, so its final writes may arrive after this transition.
            return Task.FromResult(new RebuildOutcome(updated, abandonedVersion));
        }
    }

    /// <inheritdoc/>
    Task IProjectionRebuildCoordinatorStore.DiscardStateVersionAsync(
        string projectionType, int version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);

        // No-op: in-memory projection state lives in InMemoryStateStore instances, not in
        // rows this store controls. The interface contract says reclaiming a version with
        // no rows is safe, so this is a valid implementation.
        return Task.CompletedTask;
    }
}
