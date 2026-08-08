using Alberto.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Behavioural specifications for the buffering, flush, and resync mechanics of
/// <see cref="CachingCheckpointStore"/>.
///
/// <para>
/// Covers: the <c>Ahead()</c> position-merge semantics that prevent a cache write from
/// ever lowering a position; dirty-entry monotonicity so that flush always writes the
/// furthest-advanced checkpoint; external-reset detection in both <c>FlushAsync</c> and
/// <c>ResyncFromStoreAsync</c>; timer callback exception handling; and the
/// <c>DisposeAsync</c> drain guarantee.
/// </para>
/// </summary>
public class CachingCheckpointStoreFlushAndResyncTests
{
    #region Merging a new position into a cached entry

    /// <summary>
    /// When the cache holds <c>null</c> for a processor — set by a <c>GetAsync</c> against
    /// an empty inner store — and <c>SaveAsync</c> is subsequently called for the same
    /// processor, the <c>AddOrUpdate</c> update factory fires with
    /// <c>Ahead(existing: null, position: 50)</c>. The null-left branch must return the
    /// incoming position rather than attempting to call <c>.Value</c> on a null nullable.
    ///
    /// <para>
    /// The null-cache-entry path is reachable any time a processor starts from a blank store:
    /// <c>GetAsync</c> stores the null it receives from the inner store so that the next call
    /// hits the cache instead of going to the database, then <c>SaveAsync</c> triggers the
    /// update factory. Without the null-left guard the update factory would reach
    /// <c>Math.Max(null.Value, position)</c> and throw <see cref="NullReferenceException"/>
    /// on the very first save after a cache-miss read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveAsync_AheadUpdateFactory_NullCachedEntry_ReturnsNewPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore(); // nothing stored

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // GetAsync: cache miss → inner returns null → _cache["proc"] = null (add factory).
        var fromGet = await cache.GetAsync("proc", ct);
        Assert.Null(fromGet);

        // SaveAsync: _cache["proc"] already exists (null), so the update factory fires:
        // Ahead(existing=null, position=50) → left is null → return right (50).
        await cache.SaveAsync("proc", 50L, ct);

        var result = await cache.GetAsync("proc", ct);
        Assert.Equal(50L, result);
    }

    /// <summary>
    /// When the inner store is deleted after the cache has warmed, <c>GetAsync</c> calls the
    /// update factory with <c>Ahead(cachedPosition, null)</c>. The null-right branch must
    /// return the cached position rather than attempting to call <c>.Value</c> on a null nullable.
    ///
    /// <para>
    /// This path arises when an operator resets a processor's checkpoint row directly in the
    /// database (bypassing the cache). The cache still holds the last-read position, so the
    /// update factory sees a non-null left and a null right. Without the null-right guard,
    /// <c>Math.Max(left.Value, null.Value)</c> throws <see cref="NullReferenceException"/>
    /// on the next read after such a reset.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetAsync_AheadUpdateFactory_NonNullLeft_NullRight_ReturnsLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();
        inner.Set("proc", 100L);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // Warm the cache to 100.
        await cache.GetAsync("proc", ct);

        // Delete from inner (simulates an operator reset without going through the cache).
        inner.Delete("proc");

        // Second call: cache key exists (100), inner returns null → update factory fires:
        // Ahead(100, null) → right is null → return left (100).
        var result = await cache.GetAsync("proc", ct);
        Assert.Equal(100L, result);
    }

    #endregion

    #region Dirty-entry monotonicity

    /// <summary>
    /// The dirty entry for a processor always tracks the highest position ever saved, even
    /// when a lower position arrives after a higher one. This ensures the flush writes the
    /// furthest-advanced checkpoint, never a rollback.
    ///
    /// <para>
    /// The control loop can call <c>SaveAsync</c> multiple times between flush ticks. If
    /// the dirty entry used the last position rather than the maximum, a lower position
    /// arriving after a higher one would win and the flush would write a rollback, causing
    /// the processor to replay events it has already handled.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveAsync_DirtyEntry_KeepsHigherPosition_WhenLowerFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        await cache.SaveAsync("proc", 200L, ct);
        await cache.SaveAsync("proc", 100L, ct);   // lower — must not win

        await cache.FlushAsync(ct);

        Assert.Equal(200L, await inner.GetAsync("proc", ct));
    }

    #endregion

    #region External-reset detection during flush

    /// <summary>
    /// When the inner store holds exactly the same position as the last known-persisted value,
    /// no external reset has occurred, and the flush must proceed to write the dirty entry.
    ///
    /// <para>
    /// The reset-detection guard uses <c>storePosition &gt;= persistedPosition</c>. Using
    /// strict greater-than instead would treat an equal value as a reset, drop the dirty write,
    /// and stall the processor at its last-persisted position indefinitely — every flush tick
    /// would see the same equality and discard the pending advance.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FlushAsync_WhenStoreMatchesPersisted_WritesThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();
        inner.Set("proc", 100L);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // GetAsync loads 100, populating _persisted["proc"] = 100.
        await cache.GetAsync("proc", ct);

        // Advance without touching inner (inner still has 100 = persisted).
        await cache.SaveAsync("proc", 150L, ct);

        // Flush: ApplyExternalResetIfDetectedAsync checks storePosition(100) >= persistedPosition(100)
        // → true → returns false (no reset) → write proceeds → inner gets 150.
        await cache.FlushAsync(ct);

        Assert.Equal(150L, await inner.GetAsync("proc", ct));
    }

    /// <summary>
    /// When the inner store has been externally reset below the last known-persisted position,
    /// <see cref="CachingCheckpointStore.FlushAsync"/> must drop the pending write and update
    /// the cache to the reset position.
    ///
    /// <para>
    /// Writing the buffered position on top of an operator reset would reverse the reset,
    /// causing the processor to skip ahead without reprocessing the events the reset was
    /// intended to replay. Leaving the cache at the pre-reset position after the detection
    /// would cause the processor to resume from the wrong offset on the next read.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FlushAsync_WhenExternalResetDetectedDuringFlush_DropsWriteAndUpdatesCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();
        inner.Set("proc", 100L);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // Warm cache to 100; _persisted["proc"] = 100.
        await cache.GetAsync("proc", ct);

        // Advance the cache (dirty entry at 150).
        await cache.SaveAsync("proc", 150L, ct);

        // Simulate an operator reset: inner now holds 30 (below persisted=100).
        inner.Set("proc", 30L);

        // Flush: ApplyExternalResetIfDetectedAsync detects the reset (30 < 100).
        // The flush must NOT write 150 to inner; cache must drop to 30.
        await cache.FlushAsync(ct);

        // Inner stays at 30 — the dirty write was dropped.
        Assert.Equal(30L, await inner.GetAsync("proc", ct));

        // Cache also reflects the reset position.
        Assert.Equal(30L, await cache.GetAsync("proc", ct));
    }

    #endregion

    #region Persisted-position update after resync

    /// <summary>
    /// After <see cref="CachingCheckpointStore.ResyncFromStoreAsync"/> detects an external
    /// reset and lowers the cache, <c>_persisted</c> must be updated to the new (lower)
    /// position so that subsequent flush calls see the correct baseline.
    ///
    /// <para>
    /// If <c>_persisted</c> still holds the pre-reset value after a resync, the next
    /// <c>FlushAsync</c> compares the advancing write against the stale baseline, falsely
    /// detects a second reset, and drops the write. The processor gets stuck at the reset
    /// position and cannot advance, even though the operator reset has long since been
    /// acknowledged and the inner store is accepting new values.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResyncFromStore_AfterDetectingReset_UpdatesPersistedSoSubsequentFlushSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();
        inner.Set("proc", 500L);

        await using var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        // Warm: cache and _persisted both set to 500.
        await cache.GetAsync("proc", ct);

        // Inner is externally reset to 100 (below cached=500).
        inner.Set("proc", 100L);

        // Resync detects the reset and lowers cache to 100.
        // _persisted must also be updated to 100.
        await cache.ResyncFromStoreAsync(ct);
        Assert.Equal(100L, await cache.GetAsync("proc", ct));

        // Now the processor resumes from 100 and saves 150.
        await cache.SaveAsync("proc", 150L, ct);

        // Flush: ApplyExternalResetIfDetectedAsync should see persistedPosition=100 (updated by
        // resync), storePosition=100 (inner hasn't changed), 100 >= 100 → no reset → write 150.
        // Without the persisted update: persistedPosition=500, storePosition=100, 100 < 500 →
        // falsely detected reset → write skipped, inner stays at 100.
        await cache.FlushAsync(ct);

        Assert.Equal(150L, await inner.GetAsync("proc", ct));
    }

    #endregion

    #region Resync equality guard

    /// <summary>
    /// When the inner store value equals the cached value, no external reset has occurred and
    /// no warning must be logged.
    ///
    /// <para>
    /// The reset-detection condition in <c>ResyncFromStoreAsync</c> is
    /// <c>storePosition &lt; cachedPosition</c>. Using <c>&lt;=</c> instead would treat
    /// equal values as a reset on every resync tick when nothing is wrong, emitting spurious
    /// warnings that would mask genuine resets in operator logs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResyncFromStore_WhenStoreMatchesCache_DoesNotLogWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();
        inner.Set("proc", 500L);

        var logger = new CapturingLogger<CachingCheckpointStore>();

        await using var cache = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromHours(1),
            resyncInterval: TimeSpan.FromHours(1),
            logger: logger);

        // Warm: cache = 500, inner = 500 — they are equal.
        await cache.GetAsync("proc", ct);

        // Resync: storePosition(500) < cachedPosition(500) → false → no update, no warning.
        await cache.ResyncFromStoreAsync(ct);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Empty(warnings);
    }

    #endregion

    #region Flush-timer exception handling

    /// <summary>
    /// When a timer-triggered flush throws, the exception must be caught and logged at
    /// <see cref="LogLevel.Error"/> via the supplied logger.
    ///
    /// <para>
    /// The flush timer runs on an async-void callback. An unhandled exception there would
    /// propagate to the thread pool and crash the host process. Catching and logging the
    /// error keeps the processor alive across transient storage failures; the error entry
    /// gives the operator visibility into the degraded state so the underlying issue can
    /// be investigated without losing the process.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnFlushTimer_WhenInnerSaveThrows_LogsError()
    {
        var ct = TestContext.Current.CancellationToken;
        // Throws only on the first SaveAsync so the timer-triggered flush fails (and is logged)
        // while the DisposeAsync-triggered flush can drain the dirty set cleanly.
        var inner = new ThrowOnFirstSaveStore();
        var logger = new CapturingLogger<CachingCheckpointStore>();
        var time = new FakeTimeProvider();

        await using var cache = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromMilliseconds(50),
            resyncInterval: TimeSpan.FromHours(1),
            logger: logger,
            timeProvider: time);

        // Put a dirty entry so FlushAsync actually calls inner.SaveAsync.
        await cache.SaveAsync("proc", 42L, ct);

        // Trigger OnFlushTimer.
        time.Advance(TimeSpan.FromMilliseconds(50));

        // The callback is async void — poll until the log entry arrives.
        await WaitForLogEntryAsync(logger, LogLevel.Error);

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.NotEmpty(errors);
    }

    #endregion

    #region Resync-timer exception handling

    /// <summary>
    /// When a timer-triggered resync throws, the exception must be caught and logged at
    /// <see cref="LogLevel.Error"/> via the supplied logger.
    ///
    /// <para>
    /// The resync timer runs on the same async-void pattern as the flush timer. An unhandled
    /// exception would crash the host. Catching and logging it lets the store continue
    /// operating from its cached state until the inner store recovers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnResyncTimer_WhenInnerGetThrows_LogsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new FailOnSecondGetStore();
        var logger = new CapturingLogger<CachingCheckpointStore>();
        var time = new FakeTimeProvider();

        await using var cache = new CachingCheckpointStore(
            inner,
            flushInterval: TimeSpan.FromHours(1),
            resyncInterval: TimeSpan.FromMilliseconds(50),
            logger: logger,
            timeProvider: time);

        // First GetAsync: inner.GetAsync #1 → 100 → cache["proc"] = 100 (not dirty).
        await cache.GetAsync("proc", ct);

        // Trigger OnResyncTimer.  ResyncFromStoreAsync will call inner.GetAsync #2 → throws.
        time.Advance(TimeSpan.FromMilliseconds(50));

        await WaitForLogEntryAsync(logger, LogLevel.Error);

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.NotEmpty(errors);
    }

    #endregion

    #region DisposeAsync

    /// <summary>
    /// Disposing the decorator drains whatever the flush timer has not yet written. The buffer
    /// is the whole point of the decorator, so shutdown is the one moment where losing it turns
    /// a latency optimisation into data loss: a processor that checkpointed at 100 and then had
    /// its host stopped must not restart at whatever the last timer tick happened to persist.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WritesPendingCheckpointsThroughToTheInnerStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new SimpleCheckpointStore();

        // Intervals long enough that no timer can fire — the only thing that can persist this
        // write is DisposeAsync itself.
        var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        await cache.SaveAsync("proc", 100L, ct);
        Assert.Null(await inner.GetAsync("proc", ct));

        await cache.DisposeAsync();

        Assert.Equal(100L, await inner.GetAsync("proc", ct));
    }

    /// <summary>
    /// Disposing twice is a no-op the second time. Both <c>await using</c> and an explicit
    /// <c>DisposeAsync</c> in a shutdown path are ordinary, and they coincide often enough that
    /// the second call must not re-enter the flush or dispose the timers again.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndDoesNotFlushTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new CountingSaveStore();

        var cache = new CachingCheckpointStore(
            inner, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        await cache.SaveAsync("proc", 100L, ct);

        await cache.DisposeAsync();
        var savesAfterFirstDispose = inner.SaveCount;

        await cache.DisposeAsync();

        Assert.Equal(1, savesAfterFirstDispose);
        Assert.Equal(savesAfterFirstDispose, inner.SaveCount);
    }

    /// <summary>Counts writes so a second flush is distinguishable from none.</summary>
    private sealed class CountingSaveStore : ICheckpointStore
    {
        private readonly Dictionary<string, long> _data = new();

        public int SaveCount { get; private set; }

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(processorId, out var v) ? (long?)v : null);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            SaveCount++;
            _data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _data.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Test helpers

    private static async Task WaitForLogEntryAsync(
        CapturingLogger<CachingCheckpointStore> logger,
        LogLevel level,
        int maxYields = 500)
    {
        for (var i = 0; i < maxYields; i++)
        {
            if (logger.Entries.Any(e => e.Level == level)) return;
            await Task.Yield();
        }
        Assert.Fail($"Expected a {level} log entry but none arrived after {maxYields} yields.");
    }

    /// <summary>Minimal in-memory store with mutable test control.</summary>
    private sealed class SimpleCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<string, long?> _data = new();

        public void Set(string id, long? value) => _data[id] = value;
        public void Delete(string id) => _data.Remove(id);

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            _data.TryGetValue(processorId, out var value);
            return Task.FromResult(value);
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
        {
            _data.Remove(processorId);
            return Task.CompletedTask;
        }

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
        {
            _data[processorId] = position;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Store whose <c>SaveAsync</c> throws on the first call and succeeds on all subsequent
    /// calls. The one-time throw means the timer callback catches and logs the error, while
    /// the final flush in <see cref="IAsyncDisposable.DisposeAsync"/> succeeds and drains
    /// the dirty set cleanly — making the two behaviours independently observable.
    /// </summary>
    private sealed class ThrowOnFirstSaveStore : ICheckpointStore
    {
        private int _saveCalls;

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
        {
            if (++_saveCalls == 1)
                return Task.FromException(new InvalidOperationException("simulated flush failure"));
            return Task.CompletedTask;
        }

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Store whose <c>GetAsync</c> succeeds on the first call (returns 100) and throws on
    /// every subsequent call. The split lets the cache warm successfully on the initial read
    /// while the resync timer's subsequent read fails, triggering the error-logging path
    /// under test without preventing cache initialisation.
    /// </summary>
    private sealed class FailOnSecondGetStore : ICheckpointStore
    {
        private int _getCalls;

        public Task<long?> GetAsync(string processorId, CancellationToken ct = default)
        {
            if (++_getCalls == 1)
                return Task.FromResult<long?>(100L);
            return Task.FromException<long?>(new InvalidOperationException("simulated resync failure"));
        }

        public Task SaveAsync(string processorId, long position, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ResetAsync(string processorId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RewindAsync(string processorId, long position, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// A minimal <see cref="ILogger{TCategoryName}"/> that records every log call, so tests
    /// can assert what (and whether) was logged without a mocking framework.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));
    }

    #endregion
}
