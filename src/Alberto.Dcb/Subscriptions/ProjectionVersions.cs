using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Answers "which rebuild version should this projection be reading and writing right now?"
/// for every processor in the module.
/// </summary>
/// <remarks>
/// <para>
/// State stores resolve their version on every operation, so this sits on the hot path and
/// must never touch the database there. It keeps the rebuild state cached and refreshes it
/// on a timer instead. A replica that did not initiate a rebuild still notices a promotion
/// within one refresh interval; the replica that did initiate one calls
/// <see cref="RefreshAsync"/> directly and notices immediately.
/// </para>
/// <para>
/// Until the first refresh completes, every processor reports version 1 — which is exactly
/// right for a projection that has never been rebuilt, and is why a module with no rebuild
/// store registered can skip this module entirely.
/// </para>
/// </remarks>
public sealed class ProjectionVersions : IAsyncDisposable
{
    /// <summary>
    /// The version a projection writes to before it has ever been rebuilt. Versions count up
    /// from here and are never reused, so this is also the lower bound of the range a cleanup
    /// sweep has to consider.
    /// </summary>
    public const int Initial = 1;

    /// <summary>
    /// The version selector for a projection that is not participating in rebuilds. Shared
    /// rather than allocated per store, since it is the default for almost every projection.
    /// </summary>
    public static readonly Func<int> NeverRebuilt = () => Initial;

    private readonly IProjectionRebuildStore _store;
    private readonly ConcurrentDictionary<string, ProjectionRebuildState> _cache = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _refreshLoop;

    /// <summary>
    /// Creates a version source and starts refreshing it.
    /// </summary>
    /// <param name="store">The rebuild state machine to read from.</param>
    /// <param name="refreshInterval">
    /// How often to re-read the rebuild state. Defaults to 5 seconds — the only thing a stale
    /// read delays is a passive replica noticing someone else's promotion.
    /// </param>
    public ProjectionVersions(IProjectionRebuildStore store, TimeSpan? refreshInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _refreshLoop = RefreshLoopAsync(refreshInterval ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The version readers and the live processor see. Version 1 for anything this source has
    /// no record of.
    /// </summary>
    public int ActiveVersion(string processorId)
        => _cache.TryGetValue(processorId, out var state) ? state.ActiveVersion : 1;

    /// <summary>
    /// The version a shadow rebuild loop writes into, or <see langword="null"/> when no
    /// rebuild is in flight for this processor.
    /// </summary>
    public int? RebuildingVersion(string processorId)
        => _cache.TryGetValue(processorId, out var state) ? state.RebuildingVersion : null;

    /// <summary>
    /// The cached rebuild state, or <see langword="null"/> if this source has no record of
    /// the processor.
    /// </summary>
    public ProjectionRebuildState? Current(string processorId)
        => _cache.GetValueOrDefault(processorId);

    /// <summary>
    /// A version selector to hand to a state store serving the live projection and its
    /// readers. Tracks promotions without the store having to know rebuilds exist.
    /// </summary>
    public Func<int> ForLive(string processorId) => () => ActiveVersion(processorId);

    /// <summary>
    /// The live version selector for a projection in a given module, resolved from the
    /// container. Use this when building a state store for reads outside the module builder —
    /// a query handler, say — so those reads follow a promotion instead of pinning the
    /// version that was active when the store was constructed:
    /// <code>
    /// new PostgresStateStore&lt;OrdersOverview&gt;(
    ///     dataSource, nameof(OrdersOverviewProjection), "orders",
    ///     rebuildVersion: ProjectionVersions.LiveVersion(sp, ModuleKey, nameof(OrdersOverviewProjection)));
    /// </code>
    /// </summary>
    /// <remarks>
    /// Resolves to version 1 forever in a module that has no rebuild pipeline, so it is safe to
    /// use unconditionally.
    /// </remarks>
    public static Func<int> LiveVersion(IServiceProvider services, object? moduleKey, string processorId)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetKeyedService<ProjectionVersions>(moduleKey)?.ForLive(processorId)
               ?? NeverRebuilt;
    }

    /// <summary>
    /// A version selector to hand to a state store serving a shadow rebuild loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shadow loop outlives its own rebuild by a few events — it is stopped, not killed, and
    /// a batch can still be in flight when the rebuild is promoted or aborted. Once the rebuild
    /// is gone from the state machine this keeps returning the version it was last writing to,
    /// so those last writes land in a version that is on its way out. Orphaned rows there are
    /// swept; a write to the wrong live version is not recoverable, which is why this never
    /// falls back to the active version.
    /// </para>
    /// <para>
    /// Throws if it is asked for a version before it has ever seen a rebuild in flight for the
    /// processor. That means a shadow loop was started against a stale view of the state
    /// machine — refresh before building the selector.
    /// </para>
    /// </remarks>
    public Func<int> ForShadow(string processorId)
    {
        var lastSeen = 0;

        return () =>
        {
            if (RebuildingVersion(processorId) is { } rebuilding)
            {
                Volatile.Write(ref lastSeen, rebuilding);
                return rebuilding;
            }

            var remembered = Volatile.Read(ref lastSeen);
            if (remembered != 0)
                return remembered;

            throw new InvalidOperationException(
                $"No rebuild is in flight for processor '{processorId}', so there is no version " +
                "for its shadow rebuild loop to write to. The loop was started against a stale " +
                $"view of the rebuild state machine; call {nameof(RefreshAsync)} before building " +
                "the shadow version selector.");
        };
    }

    /// <summary>
    /// Re-reads every processor's rebuild state now. Call this straight after promoting or
    /// aborting so the local state stores do not spend a refresh interval writing to a
    /// version that has just been deleted.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var states = await _store.ListAsync(ct);

        foreach (var state in states)
            _cache[state.ProcessorId] = state;

        // Processors whose rows vanished are dropped rather than left frozen at a stale
        // version; falling back to version 1 matches a never-rebuilt processor.
        var live = states.Select(s => s.ProcessorId).ToHashSet();
        foreach (var processorId in _cache.Keys.Where(k => !live.Contains(k)))
            _cache.TryRemove(processorId, out _);
    }

    private async Task RefreshLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);

        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(_stopping.Token);
                await timer.WaitForNextTickAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A failed refresh leaves the previous versions in place, which is the safe
                // stale value: it keeps writing where it was already writing. Swallowing here
                // rather than faulting the loop means a transient database blip does not
                // permanently freeze every projection's version.
                try
                {
                    await timer.WaitForNextTickAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            await _refreshLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _stopping.Dispose();
    }
}
