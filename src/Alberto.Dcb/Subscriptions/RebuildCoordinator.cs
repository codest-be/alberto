using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

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
internal sealed record RebuildCoordinatorOptions(TimeSpan PollingInterval, bool AutoPromote);

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
/// rest of the module, so enable <c>WithProcessorLeases</c> if more than one replica runs the
/// same module.
/// </para>
/// </remarks>
internal sealed class RebuildCoordinator(
    IReadOnlyList<RebuildableProjection> projections,
    IProjectionRebuildStore rebuildStore,
    ProjectionVersions versions,
    ICheckpointStore checkpoints,
    ShadowControlLoopFactory loopFactory,
    IReadOnlyList<IProjectionStateClearer> clearers,
    RebuildCoordinatorOptions options,
    ILogger<RebuildCoordinator>? logger = null) : BackgroundService
{
    private readonly Dictionary<string, ShadowLoop> _shadowLoops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RebuildStatus> _lastSeen = new(StringComparer.Ordinal);

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
        // coordinator and the version selectors it hands to shadow stores are looking at the
        // same snapshot. Reading it separately here is a correctness bug, not an efficiency
        // one: ForShadow falls back to the live version when it cannot see a rebuild in
        // flight, so a coordinator that starts a shadow loop off a fresher read than the
        // version cache has replays the whole log straight into the live projection.
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

            if (state.Status is RebuildStatus.Rebuilding)
            {
                await EnsureShadowLoopAsync(projection, state, ct);
                await CheckForCatchUpAsync(projection, state, ct);
                continue;
            }

            // Ready: the replay is complete and the rebuilt version is sitting there waiting.
            if (options.AutoPromote)
                await PromoteAsync(projection, ct);
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
        var processor = new ShadowProcessor(
            projection.CreateProcessor(versions.ForShadow(projection.ProcessorId)), shadowId);

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
    private async Task CheckForCatchUpAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        if (state.TargetPosition is not { } target ||
            state.RebuildingVersion is not { } rebuildingVersion)
        {
            return;
        }

        var shadowId = RebuildableProjection.ShadowProcessorId(
            projection.ProcessorId, rebuildingVersion);
        var position = await checkpoints.GetAsync(shadowId, ct);

        if (position is null || position < target)
            return;

        await rebuildStore.MarkReadyAsync(projection.ProcessorId, ct);
        await versions.RefreshAsync(ct);

        logger?.LogInformation(
            "Rebuild of projection {ProcessorId} reached target position {Target} and is ready to promote.",
            projection.ProcessorId, target);

        // The shadow loop keeps running past the target so the rebuilt version stays current
        // with events that arrived during the replay. It only stops at promotion, which is what
        // keeps the swap seamless.
        if (options.AutoPromote)
            await PromoteAsync(projection, ct);
    }

    /// <summary>
    /// Flips the rebuilt version to active and discards the one it replaced.
    /// </summary>
    private async Task PromoteAsync(RebuildableProjection projection, CancellationToken ct)
    {
        // Stop the shadow loop first. Its version selector would follow the promotion and keep
        // writing into the newly-active version under the shadow checkpoint, racing the live
        // loop for the same rows.
        await StopShadowLoopAsync(projection.ProcessorId, ct);

        var outcome = await rebuildStore.PromoteAsync(projection.ProcessorId, force: false, ct);

        // Before anything else: the local state stores must stop writing to the version that is
        // about to be deleted.
        await versions.RefreshAsync(ct);

        await ClearAsync(projection.ProcessorId, outcome.DiscardedVersion, ct);

        _lastSeen[projection.ProcessorId] = outcome.State.Status;

        logger?.LogInformation(
            "Projection {ProcessorId} promoted to rebuild version {Version}; version {Discarded} discarded.",
            projection.ProcessorId, outcome.State.ActiveVersion, outcome.DiscardedVersion);
    }

    /// <summary>
    /// Handles a rebuild that ended somewhere other than here — promoted or aborted from the
    /// CLI, or in another replica. The state machine and the projection state table are already
    /// consistent; anything the promotion transaction could not reach is not.
    /// </summary>
    private async Task OnSettledAsync(
        RebuildableProjection projection, ProjectionRebuildState? state, CancellationToken ct)
    {
        if (state is null)
            return;

        // Only on the transition, not on every poll: sweeping is cheap but not free.
        if (_lastSeen.TryGetValue(projection.ProcessorId, out var previous) &&
            previous == state.Status)
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
    /// in flight, the one being rebuilt.
    /// </summary>
    /// <remarks>
    /// Versions are allocated one at a time and never reused, so the whole reachable range runs
    /// from 1 to one past the highest version this processor knows about — which is the version
    /// being rebuilt when one is in flight, and the active one otherwise. Aborts leave the active
    /// version alone while still consuming numbers, so bounding on it would strand them.
    /// Clearing a version that holds nothing is a no-op, which is what lets this
    /// run without knowing what a previous coordinator got as far as doing.
    /// </remarks>
    private async Task SweepAsync(
        RebuildableProjection projection, ProjectionRebuildState state, CancellationToken ct)
    {
        // Version numbers are monotonic, so every dead version sits below the highest one this
        // processor knows about. Bounding on the active version alone would leave the versions
        // that a run of aborted rebuilds burned through unswept, since abort does not advance it.
        var highest = Math.Max(state.ActiveVersion, state.RebuildingVersion ?? state.ActiveVersion);

        for (var version = ProjectionVersions.Initial; version <= highest + 1; version++)
        {
            if (version == state.ActiveVersion || version == state.RebuildingVersion)
                continue;

            await ClearAsync(projection.ProcessorId, version, ct);
        }
    }

    private async Task ClearAsync(string processorId, int version, CancellationToken ct)
    {
        foreach (var clearer in clearers.Where(c => c.ProcessorId == processorId))
            await clearer.ClearVersionAsync(version, ct);
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
