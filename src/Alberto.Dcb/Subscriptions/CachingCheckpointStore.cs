using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Subscriptions;

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
public record FencingContext(
    string ConsumerId,
    string ReplicaId,
    bool UseProcessorLeaseFencing = false,
    Func<string, long>? FenceTokenProvider = null);

/// <summary>
/// Checkpoint store that throttles database writes by batching updates.
/// Updates are cached in-memory and periodically flushed to the underlying store.
/// This significantly reduces database load during high-throughput scenarios.
/// </summary>
internal sealed class CachingCheckpointStore : ICheckpointStore, IFencableCheckpointStore, IAsyncDisposable
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
    private readonly Timer _flushTimer;
    private readonly Timer _resyncTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
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
    public CachingCheckpointStore(
        ICheckpointStore inner,
        TimeSpan? flushInterval = null,
        TimeSpan? resyncInterval = null,
        ILogger<CachingCheckpointStore>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(30);
        _resyncInterval = resyncInterval ?? TimeSpan.FromSeconds(30);
        _logger = logger;
        _flushTimer = new Timer(OnFlushTimer, null, _flushInterval, _flushInterval);
        _resyncTimer = new Timer(OnResyncTimer, null, _resyncInterval, _resyncInterval);
    }

    public async Task<long?> GetAsync(string processorId, CancellationToken ct = default)
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
        var position = await _inner.GetAsync(processorId, ct);

        _persisted.AddOrUpdate(processorId, position, (_, concurrent) => Ahead(concurrent, position));
        return _cache.AddOrUpdate(processorId, position, (_, concurrent) => Ahead(concurrent, position));
    }

    /// <summary>
    /// The further-advanced of two positions, treating "no checkpoint yet" as behind any.
    /// </summary>
    private static long? Ahead(long? left, long? right) =>
        left is null ? right
        : right is null ? left
        : Math.Max(left.Value, right.Value);

    public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
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

        // Don't write to DB immediately - the timer will flush periodically
        return Task.CompletedTask;
    }

    public async Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        // Clear from cache and dirty set
        _cache.TryRemove(processorId, out _);
        _persisted.TryRemove(processorId, out _);
        _dirty.TryRemove(processorId, out _);

        // Reset in underlying store immediately
        await _inner.ResetAsync(processorId, ct);
    }

    public async Task RewindAsync(string processorId, long position, CancellationToken ct = default)
    {
        // Operator rewinds bypass the write buffer and go directly to the underlying store
        // so the write is unconditional and immediately durable.
        // Update all cache layers so subsequent in-process reads reflect the new position.
        _cache[processorId] = position;
        _persisted[processorId] = position;
        _dirty.TryRemove(processorId, out _);

        await _inner.RewindAsync(processorId, position, ct);
    }

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
    internal async Task ResyncFromStoreAsync(CancellationToken ct)
    {
        var cachedEntries = _cache.ToArray();

        foreach (var (processorId, cachedPosition) in cachedEntries)
        {
            // Skip entries that have pending writes — our value is ahead of the DB intentionally
            if (_dirty.ContainsKey(processorId))
                continue;

            var storePosition = await _inner.GetAsync(processorId, ct);

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
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_dirty.IsEmpty) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            // Capture the current generation inside the lock, before snapshotting dirty.
            // Entries whose stamped generation is older than flushGen were written during
            // a fence-violation race window (see SaveAsync) and must be discarded (COR-2).
            var flushGen = _cacheGeneration;
            var toFlush = _dirty.ToArray();
            var fenced = _fencingContext is not null ? _inner as IFencedCheckpointStore : null;

            // Write all dirty checkpoints FIRST, then remove only after success
            foreach (var (processorId, (position, entryGen)) in toFlush)
            {
                // Discard entries stamped with an older generation: they were written from
                // a stale cache that survived a prior fence-violation cleanup window.
                if (entryGen < flushGen)
                {
                    _dirty.TryRemove(processorId, out _);
                    continue;
                }

                if (await ApplyExternalResetIfDetectedAsync(processorId, ct))
                    continue;

                if (fenced is not null)
                {
                    var leaseHeld = await fenced.SaveIfLeaseHeldAsync(
                        processorId, position,
                        _fencingContext!.ConsumerId, _fencingContext.ReplicaId,
                        _fencingContext.FenceTokenProvider?.Invoke(processorId) ?? 0,
                        _fencingContext.UseProcessorLeaseFencing, ct);

                    if (!leaseHeld)
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
                        continue;
                    }
                }
                else
                {
                    await _inner.SaveAsync(processorId, position, ct);
                }

                _persisted[processorId] = position;

                // Only remove AFTER successful write
                _dirty.TryRemove(processorId, out _);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task<bool> ApplyExternalResetIfDetectedAsync(string processorId, CancellationToken ct)
    {
        if (!_persisted.TryGetValue(processorId, out var persistedPosition) || persistedPosition is null)
            return false;

        _cache.TryGetValue(processorId, out var cachedBefore);

        var storePosition = await _inner.GetAsync(processorId, ct);
        if (storePosition is not null && storePosition >= persistedPosition)
            return false;

        // Same compare-and-swap as the resync path: only lower the cache if nothing advanced it
        // while the read was in flight. Losing the race means the pending write goes ahead this
        // round and the next flush re-detects the reset against a settled cache — convergent,
        // and it never rolls a running processor backwards.
        if (!_cache.TryUpdate(processorId, storePosition, cachedBefore))
            return false;

        _persisted[processorId] = storePosition;
        _dirty.TryRemove(processorId, out _);

        _logger?.LogWarning(
            "Checkpoint for processor {ProcessorId} was externally reset from {PersistedPosition} to {StorePosition}; dropping pending checkpoint write",
            processorId, persistedPosition, storePosition?.ToString() ?? "null");

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _flushTimer.DisposeAsync();
        await _resyncTimer.DisposeAsync();

        // Flush any remaining dirty checkpoints
        await FlushAsync(CancellationToken.None);

        _flushLock.Dispose();
    }
}
