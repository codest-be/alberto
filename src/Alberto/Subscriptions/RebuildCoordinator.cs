using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Subscriptions;

/// <summary>
/// Builds a control loop for a shadow rebuild processor using the module's configured
/// polling, batching and middleware settings.
/// </summary>
/// <remarks>
/// The rebuild coordinator needs to construct loops long after startup, when the module
/// builder that knows how to configure them is gone. This captures that knowledge so the two
/// kinds of loop cannot drift apart.
/// </remarks>
internal sealed class ShadowControlLoopFactory(Func<IEventProcessor, ControlLoop> create)
{
    public ControlLoop Create(IEventProcessor processor) => create(processor);
}

/// <summary>
/// Options for the rebuild coordinator.
/// </summary>
/// <param name="PollingInterval">How often to re-read the rebuild state machine.</param>
/// <param name="AutoPromote">
/// Promote a rebuild as soon as it reaches the target position. When false, a finished rebuild
/// waits at <see cref="RebuildStatus.Ready"/> until an operator promotes it.
/// </param>
/// <param name="ReclaimGracePeriod">
/// How long a discarded version's state rows are left intact after the transition that
/// discarded them. Must exceed <see cref="ProjectionVersions.RefreshInterval"/>, or the sweep
/// reclaims a version while readers on other replicas still believe it is the active one.
/// </param>
internal sealed record RebuildCoordinatorOptions(
    TimeSpan PollingInterval, bool AutoPromote, TimeSpan ReclaimGracePeriod);

/// <summary>
/// Drives projection rebuilds: starts a shadow loop for every rebuild in flight, notices when
/// one has caught up, promotes it, and cleans up the version it replaced.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator owns no state of its own. Everything it does is derived from the rebuild
/// state machine in the database, so an operator can start a rebuild from the CLI in one
/// process and have it picked up here in another, and a coordinator that crashes mid-rebuild
/// resumes on restart rather than stranding it.
/// </para>
/// <para>
/// Only one replica should be running a given rebuild. Processor leases already guarantee that
/// for the live loops; the shadow loop is started under the same lease-free assumption as the
/// rest of the module, so enable leases via <c>ControlLoop:Leases:Enabled</c> if more than one
/// replica runs the same module.
/// </para>
/// </remarks>
internal sealed class RebuildCoordinator(
    IReadOnlyList<RebuildableProjection> projections,
    IProjectionRebuildCoordinatorStore rebuildStore,
    ProjectionVersions versions,
    ICheckpointStore checkpoints,
    ShadowControlLoopFactory loopFactory,
    IReadOnlyList<IProjectionStateClearer> clearers,
    RebuildCoordinatorOptions options,
    TimeProvider? timeProvider = null,
    ILogger<RebuildCoordinator>? logger = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, ShadowLoop> _shadowLoops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RebuildStatus> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// Processors whose most recent transition discarded a version that is still inside its
    /// reclaim grace period. They are swept on every tick — rather than only on a status
    /// change, which has already happened by then — until a sweep actually runs.
    /// </summary>
    private readonly HashSet<string> _pendingReclaim = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (projections.Count == 0)
            return;

        using var timer = new PeriodicTimer(options.PollingInterval);

        // A rebuild that was promoted or aborted while this replica was down left its discarded
        // version behind in any backend the promotion transaction could not reach. Sweeping
        // once at startup is what makes that self-healing.
        await SafelyAsync(() => SweepAllAsync(stoppingToken), "startup sweep");

        while (!stoppingToken.IsCancellationRequested)
        {
            await SafelyAsync(() => ReconcileAsync(stoppingToken), "reconcile");

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Brings the running shadow loops in line with what the state machine says should be
    /// running, and advances any rebuild that has finished replaying.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        // Read the state machine through ProjectionVersions rather than directly, so the
        // coordinator and the live stores it configures are looking at the same snapshot. A
        // coordinator deciding from a fresher read than the version cache has would promote a
        // version the live stores are not yet serving from.
        await versions.RefreshAsync(ct);

        foreach (var projection in projections)
        {
            var state = versions.Current(projection.ProcessorId);

            if (state is null || !state.IsRebuildInFlight)
            {
                await StopShadowLoopAsync(projection.ProcessorId, ct);
                await OnSettledAsync(projection, state, ct);
                continue;
            }

            _lastSeen[projection.ProcessorId] = state.Status;

            // Operator modules only persist intent. Completion stays here so stopping loops,
            // checking checkpoint locality, refreshing versions, handing off the live
            // checkpoint, and clearing external state remain one coordinator-owned protocol.
            if (state.RequestedAction is RebuildOperatorAction.Abort)
            {
                await AbortAsync(projection, ct);
                continue;
            }

            // The shadow loop runs for as long as the rebuild is in flight, Ready included. A
            // rebuild parked at Ready still has to track the live loop, or the copy it finally
            // publishes is stale by however long an operator took to promote it — and a replica
            // that restarted while one was already Ready would otherwise have no loop at all,
            // leaving a promotion that waits for it to catch up waiting forever.
            await EnsureShadowLoopAsync(projection, state, ct);

            if (state.Status is RebuildStatus.Rebuilding)
            {
                if (state.RequestedAction is RebuildOperatorAction.ForcePromote)
                {
                    await PromoteAsync(projection, state, force: true, ct: ct);
                    continue;
                }

                var ready = await CheckForCatchUpAsync(projection, state, ct);
                if (ready is null)
                    continue;

                state = ready;
            }

            // Ready: the replay is complete and the rebuilt version is sitting there waiting.
            if (options.AutoPromote ||
                state.RequestedAction is RebuildOperatorAction.Promote or
                    RebuildOperatorAction.ForcePromote)
            {
                await PromoteAsync(
                    projection,
                    state,
                    force: state.RequestedAction is RebuildOperatorAction.ForcePromote,
                    ct);
            }
        }
    }

    /// <summary>
    /// Starts the shadow loop for a rebuild if it is not already running. The loop replays from
    /// the start of the log under its own checkpoint key, writing into the rebuilding version.
    /// </summary>
    /// <remarks>
    /// Cached by the version being rebuilt, not just by the processor. A loop is stopped one poll
    /// after its rebuild leaves the state machine, so a second rebuild started inside that window
    /// finds the previous one still cached. Matching on the processor alone would reuse it — and
    /// its version selector is latched to the version the last rebuild was writing to, which
    /// promotion has since made the live one. The stale loop would then keep writing into the
    /// live projection, racing the live loop for the same rows.
    /// </remarks>
    private async Task EnsureShadowLoopAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        if (state.RebuildingVersion is not { } rebuildingVersion)
            return;

        if (_shadowLoops.TryGetValue(projection.ProcessorId, out var running))
        {
            if (running.Version == rebuildingVersion)
                return;

            await StopShadowLoopAsync(projection.ProcessorId, ct);
        }

        var shadowId = RebuildableProjection.ShadowProcessorId(
            projection.ProcessorId, rebuildingVersion);

        // Pinned, not resolved from the state machine. A loop exists to fill exactly one
        // version, and it is stopped one tick after that version leaves flight — so a selector
        // that followed the state machine would hand a loop still draining from an aborted
        // rebuild the *next* rebuild's version, and its replay would land on top of the one
        // already running there. Both loops would then apply the same events to the same rows.
        var processor = new ShadowProcessor(
            projection.CreateProcessor(() => rebuildingVersion), shadowId);

        var loop = loopFactory.Create(processor);
        await loop.StartAsync(ct);
        _shadowLoops[projection.ProcessorId] = new ShadowLoop(loop, rebuildingVersion);

        logger?.LogInformation(
            "Rebuild of projection {ProcessorId} started replaying under checkpoint {ShadowId}.",
            projection.ProcessorId, shadowId);
    }

    private async Task StopShadowLoopAsync(string processorId, CancellationToken ct)
    {
        if (!_shadowLoops.Remove(processorId, out var shadow))
            return;

        await shadow.Loop.StopAsync(ct);
        await shadow.Loop.DisposeAsync();
    }

    /// <summary>A running shadow loop, and the rebuilding version it was started for.</summary>
    private readonly record struct ShadowLoop(ControlLoop Loop, int Version);

    /// <summary>
    /// Moves a rebuild to <see cref="RebuildStatus.Ready"/> once its shadow checkpoint has
    /// reached the position captured when the rebuild started.
    /// </summary>
    private async Task<ProjectionRebuildState?> CheckForCatchUpAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        if (state.TargetPosition is not { } target ||
            state.RebuildingVersion is not { } rebuildingVersion)
        {
            return null;
        }

        var shadowId = RebuildableProjection.ShadowProcessorId(
            projection.ProcessorId, rebuildingVersion);
        var position = await checkpoints.GetAsync(shadowId, ct);

        if (position is null || position < target)
            return null;

        var ready = await rebuildStore.MarkReadyAsync(projection.ProcessorId, ct);
        await versions.RefreshAsync(ct);

        logger?.LogInformation(
            "Rebuild of projection {ProcessorId} reached target position {Target} and is ready to promote.",
            projection.ProcessorId, target);

        // The shadow loop keeps running past the target so the rebuilt version stays current
        // with events that arrived during the replay. It only stops at promotion.
        return ready;
    }

    /// <summary>
    /// Flips the rebuilt version to active and discards the one it replaced, once the rebuilt
    /// version is at least as current as the one it is replacing.
    /// </summary>
    private async Task PromoteAsync(
        RebuildableProjection projection,
        ProjectionRebuildState state,
        bool force,
        CancellationToken ct)
    {
        if (state.RebuildingVersion is not { } rebuildingVersion)
            return;

        var shadowId = RebuildableProjection.ShadowProcessorId(
            projection.ProcessorId, rebuildingVersion);

        var shadowPosition = await checkpoints.GetAsync(shadowId, ct) ?? 0L;
        var livePosition = await checkpoints.GetAsync(projection.ProcessorId, ct) ?? 0L;

        // Reaching the target position only means the historical replay is done. The target was
        // the head of the log when the rebuild started, and everything appended since has been
        // applied by the live loop to the version this promotion is about to delete. Flipping
        // while the shadow is still behind it publishes a copy that is missing exactly those
        // events, and nothing afterwards goes back for them — the live loop's checkpoint is
        // already past. Wait for the rebuilt version to draw level instead; the shadow loop is
        // still running and still catching up, so this settles within a poll or two.
        if (shadowPosition < livePosition)
            return;

        // Stop the shadow loop first. Its version selector would follow the promotion and keep
        // writing into the newly-active version under the shadow checkpoint, racing the live
        // loop for the same rows.
        await StopShadowLoopAsync(projection.ProcessorId, ct);

        var outcome = await rebuildStore.CompletePromotionAsync(
            projection.ProcessorId,
            force,
            ct);

        // Before anything else: the local state stores must stop writing to the version that is
        // about to be deleted.
        await versions.RefreshAsync(ct);

        // Hand the live loop the position the shadow reached. Left where it was, it would apply
        // everything between the two positions a second time — the shadow has already written
        // those events into the version that now serves reads.
        await checkpoints.SaveAsync(projection.ProcessorId, shadowPosition, ct);

        // Deliberately no clear here. The superseded version is unreachable now, but readers
        // that resolved it a moment ago are still holding its number; a later sweep reclaims it
        // once they cannot be.
        _pendingReclaim.Add(projection.ProcessorId);

        _lastSeen[projection.ProcessorId] = outcome.State.Status;

        logger?.LogInformation(
            "Projection {ProcessorId} promoted to rebuild version {Version}; version {Discarded} discarded.",
            projection.ProcessorId, outcome.State.ActiveVersion, outcome.DiscardedVersion);
    }

    /// <summary>
    /// Stops the only writer to the shadow version before atomically abandoning that version.
    /// </summary>
    private async Task AbortAsync(
        RebuildableProjection projection,
        CancellationToken ct)
    {
        await StopShadowLoopAsync(projection.ProcessorId, ct);

        var outcome = await rebuildStore.CompleteAbortAsync(projection.ProcessorId, ct);
        await versions.RefreshAsync(ct);

        // As with a promotion, the abandoned version waits for the sweep. Here the wait buys
        // something extra: a shadow loop on another replica has not necessarily noticed the
        // abort yet, and its last few writes land after this point. Reclaiming now would miss
        // them and leave the version half-populated until the next sweep anyway.
        _pendingReclaim.Add(projection.ProcessorId);

        _lastSeen[projection.ProcessorId] = outcome.State.Status;

        logger?.LogInformation(
            "Projection {ProcessorId} rebuild aborted; version {Discarded} discarded.",
            projection.ProcessorId,
            outcome.DiscardedVersion);
    }

    /// <summary>
    /// Handles a rebuild that another coordinator replica completed. The state machine and the
    /// projection state table are already consistent; anything the transaction could not reach
    /// is not.
    /// </summary>
    private async Task OnSettledAsync(
        RebuildableProjection projection, ProjectionRebuildState? state, CancellationToken ct)
    {
        if (state is null)
            return;

        // Normally only on the transition, not on every poll: sweeping is cheap but not free.
        // The exception is a processor whose discarded version is still inside its grace period
        // — its transition is already behind us, so waiting for another one would leave the
        // version to the next rebuild or the next restart.
        if (_lastSeen.TryGetValue(projection.ProcessorId, out var previous) &&
            previous == state.Status &&
            !_pendingReclaim.Contains(projection.ProcessorId))
        {
            return;
        }

        _lastSeen[projection.ProcessorId] = state.Status;

        if (state.Status is RebuildStatus.Idle)
            return;

        await versions.RefreshAsync(ct);
        await SweepAsync(projection, state, ct);
    }

    private async Task SweepAllAsync(CancellationToken ct)
    {
        await versions.RefreshAsync(ct);

        foreach (var projection in projections)
        {
            if (versions.Current(projection.ProcessorId) is not { } state)
                continue;

            _lastSeen[projection.ProcessorId] = state.Status;

            if (!state.IsRebuildInFlight)
                await SweepAsync(projection, state, ct);
        }
    }

    /// <summary>
    /// Deletes every version of a projection's state except the active one and, if a rebuild is
    /// in flight, the one being rebuilt — provided the transition that discarded them is far
    /// enough in the past that no reader can still be holding one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Versions are allocated one at a time and never reused, so the whole reachable range runs
    /// from 1 to one past the highest version this processor knows about — which is the version
    /// being rebuilt when one is in flight, and the active one otherwise. Aborts leave the active
    /// version alone while still consuming numbers, so bounding on it would strand them.
    /// Clearing a version that holds nothing is a no-op, which is what lets this
    /// run without knowing what a previous coordinator got as far as doing.
    /// </para>
    /// <para>
    /// The grace check covers the whole processor rather than just the version the last
    /// transition discarded, because the state machine does not record which version that was.
    /// Every other version in the range has been dead since some earlier rebuild, so holding
    /// them back for one more grace period costs nothing and keeps the rule to one comparison.
    /// </para>
    /// <para>
    /// <c>CompletedAt</c> comes from the database clock and the comparison is made against this
    /// host's, so a replica whose clock runs fast shortens its own grace period by the skew.
    /// The grace is a safety margin over the version-cache refresh interval, not a correctness
    /// boundary, so ordinary NTP-level skew is absorbed by the margin.
    /// </para>
    /// </remarks>
    /// <returns>
    /// True when the sweep ran; false when it was held back by the grace period and should be
    /// retried on a later tick.
    /// </returns>
    private async Task<bool> SweepAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        if (state.CompletedAt is { } completedAt &&
            _timeProvider.GetUtcNow() - completedAt < options.ReclaimGracePeriod)
        {
            _pendingReclaim.Add(projection.ProcessorId);
            return false;
        }

        // Version numbers are monotonic, so every dead version sits below the highest one this
        // processor knows about. Bounding on the active version alone would leave the versions
        // that a run of aborted rebuilds burned through unswept, since abort does not advance it.
        var highest = state.LastAllocatedVersion;

        for (var version = ProjectionVersions.Initial; version <= highest; version++)
        {
            if (version == state.ActiveVersion || version == state.RebuildingVersion)
                continue;

            await ClearAsync(projection, version, ct);
        }

        _pendingReclaim.Remove(projection.ProcessorId);
        return true;
    }

    /// <summary>
    /// Reclaims one version of one projection everywhere it is stored: the module's own state
    /// table, any backend that keeps projection state outside it, and the shadow checkpoint row
    /// that the rebuild loop for that version left behind.
    /// </summary>
    private async Task ClearAsync(
        RebuildableProjection projection, int version, CancellationToken ct)
    {
        await rebuildStore.DiscardStateVersionAsync(projection.ProjectionType, version, ct);

        foreach (var clearer in clearers.Where(c => c.ProcessorId == projection.ProcessorId))
            await clearer.ClearVersionAsync(version, ct);

        // The shadow loop advanced the checkpoint under this key while it was replaying.
        // Without this reset the row lives forever in alberto_processor_checkpoints: the live
        // processor never claims it, the orphan check's declared-processor set never contains
        // it, and a Strict policy (the non-Development default) throws on the next restart.
        await checkpoints.ResetAsync(
            RebuildableProjection.ShadowProcessorId(projection.ProcessorId, version), ct);
    }

    /// <summary>
    /// Runs one unit of coordinator work, logging and swallowing failures.
    /// </summary>
    /// <remarks>
    /// A rebuild is a background convenience; a database blip while reconciling one must not
    /// take down the host. Every operation the coordinator performs is derived from persisted
    /// state and idempotent, so the next tick picks up wherever this one stopped.
    /// </remarks>
    private async Task SafelyAsync(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Projection rebuild {What} failed; retrying on the next tick.", what);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        foreach (var processorId in _shadowLoops.Keys.ToList())
            await StopShadowLoopAsync(processorId, cancellationToken);
    }
}
