using Alberto.Subscriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Targeted tests that fill coverage gaps and kill surviving mutants in
/// <see cref="CachingCheckpointStore"/> identified by the Stryker run on
/// arch/inventory-capability-query.
///
/// <para>
/// Each test names the line(s) it targets in its XML doc so the mapping from mutant
/// to test stays legible when the mutant table is re-run.
/// </para>
/// </summary>
public class CachingCheckpointStoreCoverageTests
{
    #region Ahead() null-guard paths (lines 122–123)

    /// <summary>
    /// When the cache holds <c>null</c> for a processor (seeded by a <c>GetAsync</c> against
    /// an empty inner store) and <c>SaveAsync</c> is subsequently called for the same processor,
    /// the <c>AddOrUpdate</c> update factory in <c>SaveAsync</c> is triggered:
    /// <c>Ahead(existing=null, position=50)</c>.
    /// Kills the Conditional→false mutant at line 122: with the mutation the null-left guard
    /// (<c>left is null ? right</c>) is skipped, and the expression falls through to
    /// <c>Math.Max(null.Value, 50)</c> → <see cref="NullReferenceException"/>.
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
    /// update factory <c>Ahead(cachedPosition, null)</c> (left is non-null, right is null).
    /// Kills the Conditional→false mutant at line 123: with the mutation the null-right guard
    /// is bypassed and <c>Math.Max(left.Value, null.Value)</c> throws <see cref="NullReferenceException"/>.
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
        // With the Conditional→false mutation this becomes Math.Max(100, null.Value) → NullReferenceException.
        var result = await cache.GetAsync("proc", ct);
        Assert.Equal(100L, result);
    }

    #endregion

    #region SaveAsync dirty-entry monotonicity (line 139)

    /// <summary>
    /// The dirty entry for a processor always tracks the highest position ever saved, even
    /// when a lower position arrives after a higher one. This ensures the flush writes the
    /// furthest-advanced checkpoint, never a rollback.
    /// Kills the Math.Max→Math.Min mutant at line 139: with Min the later lower position
    /// wins and the flush writes 100 to the inner store instead of 200.
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

    #region FlushAsync external-reset detection (lines 390, 396, 407)

    /// <summary>
    /// When the inner store has the same position as <c>_persisted</c> (no external reset
    /// occurred), <see cref="CachingCheckpointStore.FlushAsync"/> must write the dirty
    /// entry through to the inner store.
    /// Kills the Equality mutant at line 396 (<c>storePosition >= persistedPosition</c> →
    /// <c>storePosition > persistedPosition</c>): with the mutation, storePosition == persistedPosition
    /// (100 == 100) is treated as a reset and the write is skipped, so inner stays at 100.
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
    /// Kills the Negate-expression mutant at line 390 (<c>!_persisted.TryGetValue</c> inverted)
    /// and the Statement mutant at line 407 (<c>_dirty.TryRemove</c> removed).
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

    #region ResyncFromStoreAsync persisted update (line 295)

    /// <summary>
    /// After <see cref="CachingCheckpointStore.ResyncFromStoreAsync"/> detects an external
    /// reset and lowers the cache, <c>_persisted</c> must be updated to the new (lower)
    /// position.  If it is not, a subsequent <see cref="CachingCheckpointStore.FlushAsync"/>
    /// will pass a stale <c>_persisted</c> value to
    /// <c>ApplyExternalResetIfDetectedAsync</c>, falsely detect another reset against the
    /// advancing write, and skip it — the processor would get stuck.
    /// Kills the Statement mutant at line 295 (<c>_persisted[processorId] = storePosition;</c>
    /// removed).
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
        // Without the fix (mutation): persistedPosition=500, storePosition=100, 100 < 500 →
        // falsely detected reset → write skipped, inner stays at 100.
        await cache.FlushAsync(ct);

        Assert.Equal(150L, await inner.GetAsync("proc", ct));
    }

    #endregion

    #region ResyncFromStoreAsync equality guard (line 292 equality mutant)

    /// <summary>
    /// When the inner store value equals the cached value, no external reset occurred and
    /// no warning must be logged.
    /// Kills the Equality mutant at line 292 (<c>storePosition &lt; cachedPosition</c> →
    /// <c>storePosition &lt;= cachedPosition</c>): with the mutation, an equal value is
    /// treated as a reset and a spurious warning is emitted.
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
        // With equality mutation: 500 <= 500 → true → TryUpdate(500, 500), _persisted updated,
        // LogWarning emitted.
        await cache.ResyncFromStoreAsync(ct);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Empty(warnings);
    }

    #endregion

    #region OnFlushTimer exception path (line 252)

    /// <summary>
    /// When a timer-triggered flush throws, the exception must be caught and logged at
    /// <see cref="LogLevel.Error"/> via the supplied logger.
    /// Kills the Statement mutant at line 252 (<c>_logger?.LogError(...)</c> removed):
    /// without the log call the error is silently swallowed and the logger has no entry.
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

    #region OnResyncTimer exception path (line 266)

    /// <summary>
    /// When a timer-triggered resync throws, the exception must be caught and logged at
    /// <see cref="LogLevel.Error"/> via the supplied logger.
    /// Kills the Statement mutant at line 266 (<c>_logger?.LogError(...)</c> removed):
    /// without the log call the error is silently swallowed and the logger has no entry.
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
    /// calls — used to exercise the flush-timer exception path (line 252).  The one-time throw
    /// means the timer callback catches and logs the error, while the final flush in
    /// <see cref="IAsyncDisposable.DisposeAsync"/> succeeds and can drain the dirty set cleanly.
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
    /// every subsequent call — used to exercise the resync-timer exception path (line 266).
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
