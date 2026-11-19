using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Alberto.Projections;
using Alberto.Subscriptions;

namespace Alberto.Checkpoints;

/// <summary>
/// Context used by <see cref="CachingCheckpointStore"/> when the underlying store supports
/// lease-fenced writes. Provides the consumer and replica identity needed to verify the
/// lease is still held at flush time.
/// </summary>
/// <param name="ConsumerId">The consumer (module) identity.</param>
/// <param name="ReplicaId">This replica's identity.</param>
/// <param name="UseProcessorLeaseFencing">Fence against processor leases rather than tenant leases.</param>
/// <param name="FenceTokenProvider">
/// Returns the fence token of the lease this replica currently holds for a processor, or 0 when
/// it holds none. Resolved per flush rather than captured once, because a replica that loses and
/// re-acquires a lease is issued a new token and must present the current one.
/// </param>
internal record FencingContext(
    string ConsumerId,
    string ReplicaId,
    bool UseProcessorLeaseFencing = false,
    Func<string, long>? FenceTokenProvider = null);

/// <summary>
/// Checkpoint store that throttles database writes by batching updates.
/// Updates are cached in-memory and periodically flushed to the underlying store.
/// This significantly reduces database load during high-throughput scenarios.
/// </summary>
internal sealed class CachingCheckpointStore : ICheckpointStore, IFencableCheckpointStore, ICheckpointInventory, IProcessorFaultStore, IAsyncDisposable
{
    private readonly ICheckpointStore _inner;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _resyncInterval;
    private readonly ILogger<CachingCheckpointStore>? _logger;
    private readonly ConcurrentDictionary<string, long?> _cache = new();
    private readonly ConcurrentDictionary<string, long?> _persisted = new();
    // Each dirty entry carries the cache generation at the time it was written.
    // On fence-violation cleanup the generation is bumped (after all three removes);
    // entries written during the race window between _dirty.TryRemove and the bump
    // carry the old generation and are discarded by the next flush (COR-2).
    private readonly ConcurrentDictionary<string, (long Position, int Generation)> _dirty = new();
    private volatile int _cacheGeneration;
    private readonly ITimer _flushTimer;
    private readonly ITimer _resyncTimer;

    // One-shot, armed by SaveSkipForwardAsync under leases; _skipFlushArmed coalesces every skip
    // in the window into the one batched flush.
    private readonly ITimer _skipFlushTimer;
    private int _skipFlushArmed;
    internal static readonly TimeSpan SkipForwardFlushDelay = TimeSpan.FromMilliseconds(100);
    // One lock per processor, so a write-through for one processor does not queue behind every
    // other processor's. A batch flush takes the locks of the ids it writes in ordinal order.
    // Never disposed: a SemaphoreSlim whose wait handle is never touched holds nothing to free,
    // and disposing one another caller may still be waiting on would throw at that caller.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private bool _disposed;
    private FencingContext? _fencingContext;

    // Primary single-callback property (backward compatibility for direct use in tests
    // and other callers that hold a concrete reference to CachingCheckpointStore).
    // For production wiring, prefer SubscribeFenceViolation so that multiple handlers
    // coexist without overwriting each other.
    /// <summary>
    /// Called with the processor ID when a fenced checkpoint write is rejected because
    /// the lease has expired. The caller should stop processing for that processor.
    /// For multi-subscriber wiring use <see cref="SubscribeFenceViolation"/>.
    /// </summary>
    public Action<string>? OnFenceViolation { get; set; }

    // Additional subscribers added through IFencableCheckpointStore.SubscribeFenceViolation.
    // These coexist with OnFenceViolation so that a per-loop cancellation handler (wired
    // by ControlLoopAssembler) and a group-stop handler (wired by ControlLoopRegistration)
    // both fire without trampling each other (COR-3).
    //
    // Copy-on-write, not a mutable List: shadow rebuild loops are assembled at runtime
    // (ShadowControlLoopFactory calls ControlLoopAssembler.Create on every rebuild start),
    // so a subscription can land while the flush loop is enumerating this array to report
    // a fence violation. Mutating a List during that foreach throws. Writers swap a new
    // array under _fenceHandlerLock; the flush loop reads one snapshot and iterates it.
    private Action<string>[] _subscribedFenceHandlers = [];
    private readonly object _fenceHandlerLock = new();

    /// <summary>
    /// Creates a new caching checkpoint store.
    /// </summary>
    /// <param name="inner">The underlying checkpoint store.</param>
    /// <param name="flushInterval">How often to flush dirty checkpoints to the database. Default is 30 seconds.</param>
    /// <param name="resyncInterval">How often to re-read checkpoints from the database to detect external resets. Default is 30 seconds.</param>
    /// <param name="logger">Optional logger for error reporting.</param>
    /// <param name="timeProvider">Clock used to schedule flush and resync. Defaults to <see cref="TimeProvider.System"/>.</param>
    public CachingCheckpointStore(
        ICheckpointStore inner,
        TimeSpan? flushInterval = null,
        TimeSpan? resyncInterval = null,
        ILogger<CachingCheckpointStore>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(30);
        _resyncInterval = resyncInterval ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        var time = timeProvider ?? TimeProvider.System;
        _flushTimer = time.CreateTimer(OnFlushTimer, null, _flushInterval, _flushInterval);
        _resyncTimer = time.CreateTimer(OnResyncTimer, null, _resyncInterval, _resyncInterval);
        _skipFlushTimer = time.CreateTimer(OnSkipFlushTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default)
    {
        // Return cached value if we have it
        if (_cache.TryGetValue(processorId, out var cached))
        {
            return cached;
        }

        // Load from store and cache. A SaveAsync can land while this read is in flight, and
        // seeding the cache with the row as it stood before that write would hand the next
        // caller a position the processor has already passed — the control loop re-streams from
        // its checkpoint every pass, so it would deliver that whole batch a second time. Neither
        // map moves backwards here; the two paths that *do* lower a checkpoint (an operator
        // rewind, and external-reset detection) are deliberate and say so.
        var position = await _inner.GetAsync(processorId, cancellationToken);

        _persisted.AddOrUpdate(processorId, position, (_, concurrent) => Ahead(concurrent, position));
        return _cache.AddOrUpdate(processorId, position, (_, concurrent) => Ahead(concurrent, position));
    }

    /// <summary>
    /// Reads the checkpoint the underlying store holds, past the cache and without touching it.
    /// </summary>
    /// <remarks>
    /// For <see cref="ProjectionCatchUp"/> on a replica that does not run the processor: no
    /// local loop advances its cache, so the only progress it can see is what the owning replica
    /// has flushed. Deliberately not merged into the cache — this is a read for a waiter, and
    /// the cache's invariants (generations, fencing cleanup, reset detection) belong to the loop.
    /// </remarks>
    internal Task<long?> GetFromStoreAsync(string processorId, CancellationToken cancellationToken) =>
        _inner.GetAsync(processorId, cancellationToken);

    /// <summary>
    /// The further-advanced of two positions, treating "no checkpoint yet" as behind any.
    /// </summary>
    private static long? Ahead(long? left, long? right) =>
        left is null ? right
        : right is null ? left
        : Math.Max(left.Value, right.Value);

    public Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default)
    {
        // Update cache immediately, monotonically. PostgresCheckpointStore's write is a
        // GREATEST, so a cache that let a position go backwards would disagree with the store
        // it fronts — and a processor whose checkpoint slips back re-delivers everything in
        // between. RewindAsync is how a checkpoint moves backwards, and it bypasses this buffer.
        _cache.AddOrUpdate(processorId, position, (_, existing) => Ahead(existing, position));

        // Stamp the current generation so FlushAsync can discard entries written during
        // a fence-violation race window (generation will have been bumped past this value).
        _dirty.AddOrUpdate(
            processorId,
            (position, _cacheGeneration),
            (_, existing) => (Math.Max(existing.Position, position), _cacheGeneration));

        // Unleased, the timer flushes periodically. Leased, the processor runs on one replica
        // and every other replica learns its progress only from the database (ProjectionCatchUp
        // waits on it), so the write goes through now, once per batch, still fenced. A shadow
        // rebuild loop is unleased and nobody waits on it, so it keeps the timer.
        return _fencingContext is null || RebuildableProjection.IsShadowProcessorId(processorId)
            ? Task.CompletedTask
            : WriteThroughAsync(processorId, cancellationToken);
    }

    /// <summary>
    /// <see cref="SaveAsync"/> for a checkpoint that only moves past events the processor does not
    /// handle (codest-be/alberto#268). Nothing was dispatched, so nothing is lost if the write lags:
    /// a crash before it lands replays from the older position, it never skips. Under leases it
    /// therefore does not write through; every skip within <see cref="SkipForwardFlushDelay"/>
    /// rides one batched flush, so an idle store with many processors costs one round trip per
    /// window instead of one per processor per head advance. Unleased, it is the timer's, as before.
    /// </summary>
    internal Task SaveSkipForwardAsync(string processorId, long position)
    {
        _cache.AddOrUpdate(processorId, position, (_, existing) => Ahead(existing, position));
        _dirty.AddOrUpdate(
            processorId,
            (position, _cacheGeneration),
            (_, existing) => (Math.Max(existing.Position, position), _cacheGeneration));

        if (_fencingContext is not null && !RebuildableProjection.IsShadowProcessorId(processorId)
            && Interlocked.Exchange(ref _skipFlushArmed, 1) == 0)
            _skipFlushTimer.Change(SkipForwardFlushDelay, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when this replica holds the processor's lease, so its cache is the processor's
    /// progress and the store has nothing newer to tell a waiter.
    /// </summary>
    internal bool HoldsLease(string processorId) =>
        _fencingContext?.FenceTokenProvider?.Invoke(processorId) > 0;

    private void OnSkipFlushTimer(object? state)
    {
        // Disarm first: a skip landing during the flush re-arms, so none waits for the 30 s timer.
        Interlocked.Exchange(ref _skipFlushArmed, 0);
        OnFlushTimer(state);
    }

    private SemaphoreSlim LockFor(string processorId) =>
        _locks.GetOrAdd(processorId, static _ => new SemaphoreSlim(1, 1));

    private async Task WriteThroughAsync(string processorId, CancellationToken cancellationToken)
    {
        var gate = LockFor(processorId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!_dirty.TryGetValue(processorId, out var entry))
                return;
            if (_persisted.TryGetValue(processorId, out var persisted) && persisted >= entry.Position)
                return;

            await WriteEntriesAsync([new(processorId, entry)], _cacheGeneration, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The batch is processed; failing the save would redeliver it. The entry stays
            // dirty and the timer retries, as it would have without the write-through.
            _logger?.LogError(ex, "Failed to write checkpoint for processor {ProcessorId}; the periodic flush will retry", processorId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops what this replica remembers of a processor's checkpoint, so the next read comes from
    /// the store. Called when a lease is acquired: another replica may have advanced the
    /// processor since this one last ran it, and the cache only ever moves forward on its own
    /// writes, so a remembered position would replay everything in between.
    /// </summary>
    internal async Task ForgetAsync(string processorId, CancellationToken cancellationToken)
    {
        // Under the processor's lock, as in ResetAsync: a flush in flight must not put the entry back.
        var gate = LockFor(processorId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            _cache.TryRemove(processorId, out _);
            _persisted.TryRemove(processorId, out _);
            _dirty.TryRemove(processorId, out _);
        }
        finally { gate.Release(); }
    }

    public async Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
    {
        // Under the processor's lock: a flush that snapshotted the dirty entry before this reset
        // would otherwise write the stale position after it.
        var gate = LockFor(processorId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            _cache.TryRemove(processorId, out _);
            _persisted.TryRemove(processorId, out _);
            _dirty.TryRemove(processorId, out _);

            // Reset in underlying store immediately
            await _inner.ResetAsync(processorId, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default)
    {
        // Operator rewinds bypass the write buffer and go directly to the underlying store
        // so the write is unconditional and immediately durable.
        // Update all cache layers so subsequent in-process reads reflect the new position.
        // Under the processor's lock, as in ResetAsync: an in-flight flush must not write the
        // pre-rewind position over this one.
        var gate = LockFor(processorId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            _cache[processorId] = position;
            _persisted[processorId] = position;
            _dirty.TryRemove(processorId, out _);

            await _inner.RewindAsync(processorId, position, cancellationToken);
        }
        finally { gate.Release(); }
    }

    // Fault records bypass the write buffer entirely: a fault write happens once, on the way
    // down, and must be durable — buffering it behind the flush timer would lose it in exactly
    // the crash it exists to explain. A no-op when the inner store has no fault support.
    public Task RecordFaultAsync(string processorId, ProcessorFaultRecord fault, CancellationToken cancellationToken = default)
        => _inner is IProcessorFaultStore f ? f.RecordFaultAsync(processorId, fault, cancellationToken) : Task.CompletedTask;

    public Task ClearFaultAsync(string processorId, CancellationToken cancellationToken = default)
        => _inner is IProcessorFaultStore f ? f.ClearFaultAsync(processorId, cancellationToken) : Task.CompletedTask;

    public Task<ProcessorFaultRecord?> GetFaultAsync(string processorId, CancellationToken cancellationToken = default)
        => _inner is IProcessorFaultStore f
            ? f.GetFaultAsync(processorId, cancellationToken)
            : Task.FromResult<ProcessorFaultRecord?>(null);

    /// <summary>
    /// Sets the fencing context so that periodic flushes use lease-fenced writes when the
    /// underlying store implements <see cref="IFencedCheckpointStore"/>.
    /// </summary>
    public void SetFencingContext(FencingContext ctx) => _fencingContext = ctx;

    /// <summary>
    /// Appends <paramref name="handler"/> to the set of callbacks invoked when a fenced
    /// checkpoint write is rejected. All registered handlers and <see cref="OnFenceViolation"/>
    /// are called together; registrations are additive and process-lifetime.
    /// Safe to call while a flush is in progress.
    /// </summary>
    public void SubscribeFenceViolation(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_fenceHandlerLock)
            _subscribedFenceHandlers = [.. _subscribedFenceHandlers, handler];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>false</c> when the inner store does not implement
    /// <see cref="ICheckpointInventory"/>. This lets the DI resolution site use a plain
    /// property-pattern match (<c>is ICheckpointInventory { CanEnumerate: true }</c>) without
    /// naming any concrete decorator type, so third-party decorators can express the same
    /// opt-out through the public interface rather than requiring Alberto internals.
    /// </remarks>
    public bool CanEnumerate => _inner is ICheckpointInventory;

    /// <inheritdoc />
    /// <remarks>
    /// Flushes all pending writes to the inner store before listing, so a processor whose
    /// checkpoint was written via <see cref="SaveAsync"/> but not yet timer-flushed is still
    /// visible. After the flush, any writes that raced in during the flush window remain in
    /// <c>_dirty</c> and are unioned into the result so the caller sees a complete picture.
    /// <para>
    /// Throws <see cref="NotSupportedException"/> when the inner store does not implement
    /// <see cref="ICheckpointInventory"/> (i.e. when <see cref="CanEnumerate"/> is
    /// <c>false</c>). Well-behaved callers check <see cref="CanEnumerate"/> first; this
    /// exception is a backstop for code that bypasses that check.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken cancellationToken = default)
    {
        // Guard first: returning an empty list when the inner store cannot enumerate would
        // be a false all-clear under a Strict orphan policy — indistinguishable from "all
        // processors accounted for" rather than "we simply don't know". Callers should check
        // CanEnumerate before calling this method; the throw is a backstop for those that don't.
        if (_inner is not ICheckpointInventory innerInventory)
            throw new NotSupportedException(
                $"{nameof(CachingCheckpointStore)}: the wrapped {_inner.GetType().Name} does not " +
                $"implement {nameof(ICheckpointInventory)}. Check {nameof(CanEnumerate)} before " +
                "calling this method.");

        // Drive pending writes into the inner store before listing. Without this, a processor
        // whose checkpoint was SaveAsync'd but not yet timer-flushed would be absent from the
        // inner store's snapshot — making a live, actively-writing processor look like it has
        // no row, and causing the orphan check to miss a rename that only affected that processor.
        await FlushAsync(cancellationToken);

        var fromStore = await innerInventory.ListProcessorIdsAsync(cancellationToken);

        // Narrow race window: a SaveAsync that arrived after FlushAsync's internal snapshot
        // was taken is still in _dirty and has not yet reached the inner store. Union those
        // keys in so the caller sees every processor this store has touched in its lifetime.
        var remainingDirty = _dirty.Keys.ToList();
        if (remainingDirty.Count == 0)
            return fromStore;

        var union = new HashSet<string>(fromStore, StringComparer.Ordinal);
        union.UnionWith(remainingDirty);
        return [.. union];
    }

    private async void OnFlushTimer(object? state)
    {
        if (_disposed) return;

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to flush checkpoints to underlying store");
        }
    }

    private async void OnResyncTimer(object? state)
    {
        if (_disposed) return;

        try
        {
            await ResyncFromStoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to resync checkpoints from underlying store");
        }
    }

    /// <summary>
    /// Re-reads all cached checkpoints from the underlying store and detects external resets.
    /// If the database value is lower than the cached value (and the entry is not dirty),
    /// the cache is updated to the database value so the control loop replays from there.
    /// </summary>
    internal async Task ResyncFromStoreAsync(CancellationToken cancellationToken)
    {
        // Skip entries that have pending writes — our value is ahead of the DB intentionally
        var cachedEntries = _cache.ToArray().Where(e => !_dirty.ContainsKey(e.Key)).ToArray();
        if (cachedEntries.Length == 0)
            return;

        var stored = await GetManyAsync(cachedEntries.Select(e => e.Key).ToArray(), cancellationToken);

        foreach (var (processorId, cachedPosition) in cachedEntries)
        {
            var storePosition = stored.GetValueOrDefault(processorId);

            // Detect external reset: DB value is lower than (or deleted vs) our cached value.
            // Swapped against the value this pass read rather than assigned: a SaveAsync that
            // landed while the store read was in flight is a processor advancing past the point
            // we are about to lower it to, not an external reset, and lowering it anyway would
            // make the control loop redeliver everything in between.
            if (cachedPosition is not null && (storePosition is null || storePosition < cachedPosition)
                && _cache.TryUpdate(processorId, storePosition, cachedPosition))
            {
                _persisted[processorId] = storePosition;
                _logger?.LogWarning(
                    "Checkpoint for processor {ProcessorId} was externally reset from {CachedPosition} to {StorePosition}",
                    processorId, cachedPosition, storePosition?.ToString() ?? "null");
            }
        }
    }

    /// <summary>
    /// Flushes all dirty checkpoints to the underlying store.
    /// When a fencing context is set and the inner store implements
    /// <see cref="IFencedCheckpointStore"/>, each write is guarded by a lease check.
    /// If a fenced write is rejected, <see cref="OnFenceViolation"/> is invoked for that
    /// processor ID and the checkpoint entry is removed from the dirty set without writing.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_dirty.IsEmpty) return;

        // Ordinal order, so two flushes (timer, dispose, inventory) never wait on each other's
        // locks in a cycle; every other path holds a single processor's lock.
        var processorIds = _dirty.Keys.Order(StringComparer.Ordinal).ToArray();
        var held = new List<SemaphoreSlim>(processorIds.Length);
        try
        {
            foreach (var processorId in processorIds)
            {
                var gate = LockFor(processorId);
                await gate.WaitAsync(cancellationToken);
                held.Add(gate);
            }

            // Capture the current generation under the locks, before snapshotting dirty.
            // Entries whose stamped generation is older than flushGen were written during
            // a fence-violation race window (see SaveAsync) and must be discarded (COR-2).
            var flushGen = _cacheGeneration;
            var entries = new List<KeyValuePair<string, (long Position, int Generation)>>(processorIds.Length);
            foreach (var processorId in processorIds)
                if (_dirty.TryGetValue(processorId, out var entry))
                    entries.Add(new(processorId, entry));

            await WriteEntriesAsync(entries, flushGen, cancellationToken);
        }
        finally
        {
            foreach (var gate in held)
                gate.Release();
        }
    }

    // Writes dirty entries, one batch per fencing mode. Caller holds each entry's processor lock.
    private async Task WriteEntriesAsync(
        IReadOnlyList<KeyValuePair<string, (long Position, int Generation)>> entries,
        int flushGen, CancellationToken cancellationToken)
    {
        var fencedWrites = new List<CheckpointWrite>();
        var plainWrites = new List<CheckpointWrite>();
        var pending = new Dictionary<string, (long? CachedBefore, (long Position, int Generation) Entry)>(
            entries.Count, StringComparer.Ordinal);

        foreach (var (processorId, entry) in entries)
        {
            // Discard entries stamped with an older generation: they were written from
            // a stale cache that survived a prior fence-violation cleanup window.
            if (entry.Generation < flushGen)
            {
                _dirty.TryRemove(new(processorId, entry));
                continue;
            }

            // Captured before the write, so a Reset outcome lowers the cache only if nothing
            // advanced it meanwhile (the same compare-and-swap as the resync path).
            _cache.TryGetValue(processorId, out var cached);
            pending[processorId] = (cached, entry);

            // The floor is what this replica last persisted. A stored row below it is an
            // operator reset or rewind, which the write must not lift back up.
            var floor = _persisted.TryGetValue(processorId, out var persisted) ? persisted : null;

            // A shadow rebuild loop is not in the lease group, so no lease exists for its
            // id and a fenced write would be rejected forever.
            // ponytail: shadow loops are unleased, so two replicas can replay one rebuild
            // (idempotent, checkpoint is GREATEST); lease the shadow id if that ever costs.
            if (_fencingContext is { } fencing && !RebuildableProjection.IsShadowProcessorId(processorId))
                fencedWrites.Add(new(processorId, entry.Position, floor,
                    fencing.FenceTokenProvider?.Invoke(processorId) ?? 0));
            else
                plainWrites.Add(new(processorId, entry.Position, floor, 0));
        }

        if (fencedWrites.Count > 0)
            Apply(fencedWrites, await SaveManyAsync(fencedWrites, _fencingContext, cancellationToken));
        if (plainWrites.Count > 0)
            Apply(plainWrites, await SaveManyAsync(plainWrites, null, cancellationToken));

        void Apply(List<CheckpointWrite> writes, IReadOnlyDictionary<string, CheckpointWriteOutcome> outcomes)
        {
            foreach (var write in writes)
            {
                var processorId = write.ProcessorId;
                if (!outcomes.TryGetValue(processorId, out var outcome))
                    continue;

                switch (outcome.Result)
                {
                    case CheckpointWriteResult.Written:
                        _persisted[processorId] = write.Position;
                        // Only the entry that was written: a SaveAsync that raced in while the
                        // write was in flight replaced it, and stays dirty for the next flush.
                        _dirty.TryRemove(new(processorId, pending[processorId].Entry));
                        break;

                    case CheckpointWriteResult.Reset:
                        ApplyExternalReset(processorId, write.Floor, outcome.StoredPosition, pending[processorId].CachedBefore);
                        break;

                    case CheckpointWriteResult.Fenced:
                        OnFencedOut(processorId);
                        break;

                    case CheckpointWriteResult.Retry:
                        break;
                }
            }
        }
    }

    private void ApplyExternalReset(string processorId, long? persistedPosition, long? storePosition, long? cachedBefore)
    {
        // Same compare-and-swap as the resync path: only lower the cache if nothing advanced it
        // while the write was in flight. Losing the race leaves the entry dirty and unwritten,
        // and the next flush re-detects the reset against a settled cache: convergent, and it
        // never rolls a running processor backwards nor writes over the operator's reset.
        if (!_cache.TryUpdate(processorId, storePosition, cachedBefore))
            return;

        _persisted[processorId] = storePosition;
        _dirty.TryRemove(processorId, out _);

        _logger?.LogWarning(
            "Checkpoint for processor {ProcessorId} was externally reset from {PersistedPosition} to {StorePosition}; dropping pending checkpoint write",
            processorId, persistedPosition, storePosition?.ToString() ?? "null");
    }

    private void OnFencedOut(string processorId)
    {
        _logger?.LogWarning(
            "Fenced checkpoint flush rejected for processor {ProcessorId} — lease expired",
            processorId);
        // Remove all three dictionaries first, then bump the generation.
        // Any concurrent SaveAsync that races into the window between
        // _dirty.TryRemove and the Increment captures the old generation;
        // the next flush filters those entries via entryGen < flushGen (COR-2).
        _dirty.TryRemove(processorId, out _);
        // Reset the cache so the next GetAsync re-reads the true DB value.
        // Keeping the stale high-water mark would cause the processor to skip
        // events between the real DB checkpoint and the cached position.
        _cache.TryRemove(processorId, out _);
        _persisted.TryRemove(processorId, out _);
        Interlocked.Increment(ref _cacheGeneration);
        // Invoke the primary handler first, then every additive subscriber.
        // Read one snapshot: a concurrent SubscribeFenceViolation swaps the
        // array rather than mutating it, so this enumeration stays valid.
        OnFenceViolation?.Invoke(processorId);
        foreach (var h in Volatile.Read(ref _subscribedFenceHandlers))
            h(processorId);
    }

    private async Task<IReadOnlyDictionary<string, long?>> GetManyAsync(
        IReadOnlyCollection<string> processorIds, CancellationToken cancellationToken)
    {
        if (_inner is ICheckpointBatchStore batch)
            return await batch.GetManyAsync(processorIds, cancellationToken);

        var stored = new Dictionary<string, long?>(processorIds.Count, StringComparer.Ordinal);
        foreach (var processorId in processorIds)
            stored[processorId] = await _inner.GetAsync(processorId, cancellationToken);
        return stored;
    }

    // One statement when the store can batch. Otherwise the per-processor path: read to detect a
    // reset, then write, fenced when the store can fence.
    private async Task<IReadOnlyDictionary<string, CheckpointWriteOutcome>> SaveManyAsync(
        IReadOnlyList<CheckpointWrite> writes, FencingContext? fencing, CancellationToken cancellationToken)
    {
        if (_inner is ICheckpointBatchStore batch)
            return await batch.SaveManyAsync(writes, fencing, cancellationToken);

        var outcomes = new Dictionary<string, CheckpointWriteOutcome>(writes.Count, StringComparer.Ordinal);
        foreach (var write in writes)
        {
            if (write.Floor is not null)
            {
                var stored = await _inner.GetAsync(write.ProcessorId, cancellationToken);
                if (stored is null || stored < write.Floor)
                {
                    outcomes[write.ProcessorId] = new(CheckpointWriteResult.Reset, stored);
                    continue;
                }
            }

            if (fencing is not null && _inner is IFencedCheckpointStore fenced)
            {
                var leaseHeld = await fenced.SaveIfLeaseHeldAsync(
                    write.ProcessorId, write.Position, fencing.ConsumerId, fencing.ReplicaId,
                    write.FenceToken, fencing.UseProcessorLeaseFencing, cancellationToken);
                outcomes[write.ProcessorId] = new(leaseHeld ? CheckpointWriteResult.Written : CheckpointWriteResult.Fenced);
            }
            else
            {
                await _inner.SaveAsync(write.ProcessorId, write.Position, cancellationToken);
                outcomes[write.ProcessorId] = new(CheckpointWriteResult.Written);
            }
        }

        return outcomes;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        await _resyncTimer.DisposeAsync();
        await _skipFlushTimer.DisposeAsync();

        // Flush any remaining dirty checkpoints
        await FlushAsync(CancellationToken.None);
    }
}
