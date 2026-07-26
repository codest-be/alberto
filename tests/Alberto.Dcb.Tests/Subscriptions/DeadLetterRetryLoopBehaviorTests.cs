using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Behavioral tests for <see cref="DeadLetterRetryLoop"/>.
/// Covers:
/// <list type="bullet">
///   <item>claim → dispatch → remove on success (TEST-2)</item>
///   <item>claim → dispatch → abandon on handler throw (TEST-2)</item>
///   <item>per-entry DI scope creation and tenant-context injection (P0.6 / TEN-5)</item>
/// </list>
/// All tests are in-process (no Postgres, no Testcontainers).
/// </summary>
public sealed class DeadLetterRetryLoopBehaviorTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal <see cref="DeadLetterEntry"/> suitable for seeding a store.
    /// <paramref name="retryRequested"/> is <see langword="true"/> by default so the
    /// loop's <c>ClaimRetryRequestedAsync</c> filter will pick the entry up.
    /// </summary>
    private static DeadLetterEntry MakeEntry(
        string processorId,
        string? tenantId = null,
        bool retryRequested = true) =>
        new(
            Id: Guid.NewGuid(),
            ProcessorId: processorId,
            EventId: Guid.NewGuid(),
            EventType: "test-event",
            EventData: "{}",
            ErrorMessage: "previous failure",
            StackTrace: null,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow,
            GlobalPosition: 1,
            RetryRequested: retryRequested,
            TenantId: tenantId,
            Tags: null,
            Metadata: null,
            CreatedAt: DateTime.UtcNow);

    // ── test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Records every event dispatched and completes <see cref="WhenProcessed"/> after the first.
    /// </summary>
    private sealed class CapturingProcessor(string processorId) : IEventProcessor
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IEventEnvelope> Processed { get; } = [];
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Completes when the first event has been processed.</summary>
        public Task WhenProcessed => _tcs.Task;

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            Processed.Add(@event);
            _tcs.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Calls a callback after each processed event; intended for multi-entry tests where
    /// the caller counts completions to know when all entries have been handled.
    /// </summary>
    private sealed class CountingProcessor(string processorId, Action onEach) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        {
            onEach();
            return Task.CompletedTask;
        }
    }

    /// <summary>Always throws so the retry loop takes the abandon path.</summary>
    private sealed class FailingProcessor(string processorId) : IEventProcessor
    {
        public string ProcessorId { get; } = processorId;
        public bool IsActive { get; set; } = true;
        public bool IsRebuilding { get; set; }
        public IReadOnlySet<string> HandledEventTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated dispatch failure");
    }

    /// <summary>
    /// Decorator over <see cref="InMemoryDeadLetterStore"/> that records every
    /// <see cref="IDeadLetterStore.AbandonRetryAsync"/> call and signals
    /// <see cref="WhenAbandoned"/> so tests can await the abandon without wall-clock delays.
    /// Uses composition (not inheritance) because <see cref="InMemoryDeadLetterStore"/>
    /// methods are not virtual and interface dispatch would bypass a derived <c>new</c> member.
    /// </summary>
    private sealed class TrackingDeadLetterStore : IDeadLetterStore
    {
        private readonly InMemoryDeadLetterStore _inner = new();
        private readonly TaskCompletionSource _abandonTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Guid> AbandonedIds { get; } = [];

        /// <summary>Completes when the first <see cref="AbandonRetryAsync"/> call is received.</summary>
        public Task WhenAbandoned => _abandonTcs.Task;

        public Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
            => _inner.StoreAsync(entry, ct);

        public Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
            string processorId, string? tenantId = null, int limit = 100, CancellationToken ct = default)
            => _inner.GetAsync(processorId, tenantId, limit, ct);

        public Task<int> CountAsync(string processorId, CancellationToken ct = default)
            => _inner.CountAsync(processorId, ct);

        public Task<bool> CompleteRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
            => _inner.CompleteRetryAsync(claim, ct);

        public Task ClearAsync(string processorId, CancellationToken ct = default)
            => _inner.ClearAsync(processorId, ct);

        public Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
            => _inner.MarkForRetryAsync(processorId, ct);

        public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
            string processorId, int batchSize, TimeSpan leaseDuration,
            string claimedBy, CancellationToken ct = default)
            => _inner.ClaimRetryRequestedAsync(processorId, batchSize, leaseDuration, claimedBy, ct);

        public Task<bool> AbandonRetryAsync(DeadLetterClaim claim, CancellationToken ct = default)
        {
            AbandonedIds.Add(claim.Entry.Id);
            _abandonTcs.TrySetResult();
            return _inner.AbandonRetryAsync(claim, ct);
        }
    }

    /// <summary>
    /// Spy <see cref="IServiceScopeFactory"/> that records how many scopes were created
    /// and what <see cref="TenantContext.TenantId"/> was set on each scope's
    /// <see cref="TenantContext"/> before the scope was disposed.
    /// This lets tests verify the P0.6 (TEN-5) per-entry tenant-scope behaviour without
    /// depending on the processor receiving the tenant context directly (the processor is
    /// a singleton and is not resolved from the per-retry scope in the current implementation).
    /// </summary>
    private sealed class TenantCapturingScopeFactory : IServiceScopeFactory
    {
        /// <summary>
        /// Tenant IDs captured at scope-dispose time, one entry per created scope.
        /// <see langword="null"/> means the scope was disposed before <c>SetTenant</c> was called.
        /// </summary>
        public List<string?> ScopedTenants { get; } = [];

        public int ScopesCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return new TenantCapturingScope(ScopedTenants);
        }

        private sealed class TenantCapturingScope : IServiceScope, IAsyncDisposable
        {
            private readonly TenantContext _tenantContext = new();
            private readonly List<string?> _captured;
            private bool _disposed;

            public TenantCapturingScope(List<string?> captured)
            {
                _captured = captured;
                ServiceProvider = new SimpleTenantServiceProvider(_tenantContext);
            }

            public IServiceProvider ServiceProvider { get; }

            // Idempotent capture: AsyncServiceScope prefers DisposeAsync when available,
            // but we guard against both being called to avoid duplicate list entries.
            private void Capture()
            {
                if (_disposed) return;
                _disposed = true;
                _captured.Add(_tenantContext.TenantId);
            }

            public void Dispose() => Capture();

            public ValueTask DisposeAsync()
            {
                Capture();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class SimpleTenantServiceProvider(TenantContext tenantContext) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(TenantContext) ? tenantContext : null;
        }
    }

    // ── claim → dispatch → remove (success path) ─────────────────────────────

    [Fact]
    public async Task ClaimThenRemove_WhenProcessorSucceeds_EntryIsGoneAfterLoop()
    {
        // Arrange
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-remove");
        var entry = MakeEntry("proc-remove");
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMinutes(1));

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        // Wait for dispatch to complete, then allow DisposeAsync to drive the loop to
        // completion (which ensures RemoveAsync has been called before we assert).
        await processor.WhenProcessed.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — processor received the event
        var processedEvent = Assert.Single(processor.Processed);
        Assert.Equal(entry.EventId, processedEvent.Id);

        // Assert — entry has been removed from the dead-letter store
        var remaining = await store.GetAsync("proc-remove");
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task ClaimThenRemove_ReconstructedEnvelope_HasCorrectFields()
    {
        // Arrange — verify the envelope delivered to the processor matches the dead-letter entry
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-envelope");
        var entry = MakeEntry("proc-envelope", tenantId: null);
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(processor, store, pollingInterval: TimeSpan.FromMinutes(1));

        await loop.StartAsync(TestContext.Current.CancellationToken);
        await processor.WhenProcessed.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        var evt = Assert.Single(processor.Processed);
        Assert.Equal(entry.EventId, evt.Id);
        Assert.Equal(entry.EventType, evt.EventType.Id);
        Assert.Equal(entry.EventData, evt.EventData);
        Assert.Equal(entry.GlobalPosition, evt.GlobalPosition);
    }

    // ── claim → dispatch → abandon (failure path) ────────────────────────────

    [Fact]
    public async Task ClaimThenAbandon_WhenProcessorThrows_AbandonRetryAsyncIsCalled()
    {
        // Arrange
        var store = new TrackingDeadLetterStore();
        var processor = new FailingProcessor("proc-abandon");
        var entry = MakeEntry("proc-abandon");
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(processor, store, pollingInterval: TimeSpan.FromMinutes(1));

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await store.WhenAbandoned.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — AbandonRetryAsync was called for this entry's id
        var abandonedId = Assert.Single(store.AbandonedIds);
        Assert.Equal(entry.Id, abandonedId);
    }

    [Fact]
    public async Task ClaimThenAbandon_WhenProcessorThrows_EntryRemainsWithRetryCleared()
    {
        // Arrange
        var store = new TrackingDeadLetterStore();
        var processor = new FailingProcessor("proc-retain");
        var entry = MakeEntry("proc-retain");
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(processor, store, pollingInterval: TimeSpan.FromMinutes(1));

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await store.WhenAbandoned.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — entry still present (not removed on failure)
        var remaining = await store.GetAsync("proc-retain");
        var surviving = Assert.Single(remaining);
        Assert.Equal(entry.Id, surviving.Id);

        // Assert — RetryRequested cleared and claim columns reset (entry is in "idle" state again)
        Assert.False(surviving.RetryRequested);
        Assert.Null(surviving.ClaimedAt);
        Assert.Null(surviving.ClaimExpiresAt);
        Assert.Null(surviving.ClaimedBy);
    }

    // ── P0.6 (TEN-5): per-entry tenant-scope dispatch ────────────────────────

    [Fact]
    public async Task TenantScope_CreatedPerEntry_WithCorrectTenantId()
    {
        // Arrange — entry with a tenant; scope factory spy tracks what tenant was set
        const string tenantId = "acme";
        var spy = new TenantCapturingScopeFactory();
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-tenant");
        var entry = MakeEntry("proc-tenant", tenantId: tenantId);
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMinutes(1),
            scopeFactory: spy);

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await processor.WhenProcessed.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — exactly one scope created for the one entry
        Assert.Equal(1, spy.ScopesCreated);

        // Assert — the TenantContext inside that scope had the correct tenant set before disposal
        var captured = Assert.Single(spy.ScopedTenants);
        Assert.Equal(tenantId, captured);
    }

    [Fact]
    public async Task TenantScope_NotCreated_WhenEntryHasNoTenantId()
    {
        // Arrange — entry without a tenant; scope should not be created (single-tenant path)
        var spy = new TenantCapturingScopeFactory();
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-notenant");
        var entry = MakeEntry("proc-notenant", tenantId: null);
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMinutes(1),
            scopeFactory: spy);

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await processor.WhenProcessed.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — no scope created; event still dispatched via the plain path
        Assert.Equal(0, spy.ScopesCreated);
        Assert.Single(processor.Processed);
    }

    [Fact]
    public async Task TenantScope_NotCreated_WhenNoScopeFactory()
    {
        // Arrange — entry WITH a tenant but no scopeFactory supplied; must not throw
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-nofactory");
        var entry = MakeEntry("proc-nofactory", tenantId: "acme");
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        // No scopeFactory parameter — falls through to plain dispatch path.
        var loop = new DeadLetterRetryLoop(processor, store, pollingInterval: TimeSpan.FromMinutes(1));

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await processor.WhenProcessed.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — event dispatched despite having a tenant id and no factory
        Assert.Single(processor.Processed);
    }

    [Fact]
    public async Task TenantScope_EachEntryGetsItsOwnScope_WithCorrectTenant()
    {
        // Arrange — two entries from different tenants in a single batch
        var spy = new TenantCapturingScopeFactory();
        var store = new InMemoryDeadLetterStore();
        int processed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new CountingProcessor("proc-multitenant", () =>
        {
            if (Interlocked.Increment(ref processed) >= 2)
                allDone.TrySetResult();
        });

        var entryA = MakeEntry("proc-multitenant", tenantId: "tenant_a");
        var entryB = MakeEntry("proc-multitenant", tenantId: "tenant_b");
        await store.StoreAsync(entryA, TestContext.Current.CancellationToken);
        await store.StoreAsync(entryB, TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMinutes(1),
            batchSize: 10,
            scopeFactory: spy);

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DisposeAsync();

        // Assert — one scope per entry
        Assert.Equal(2, spy.ScopesCreated);

        // Assert — each scope captured its respective tenant (order may vary)
        Assert.Contains("tenant_a", spy.ScopedTenants);
        Assert.Contains("tenant_b", spy.ScopedTenants);
    }

    // ── batch / multi-entry ───────────────────────────────────────────────────

    [Fact]
    public async Task MultipleEntries_AllRemovedAfterSuccessfulProcessing()
    {
        // Arrange
        var store = new InMemoryDeadLetterStore();
        const int total = 3;
        int processed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = new CountingProcessor("proc-batch", () =>
        {
            if (Interlocked.Increment(ref processed) >= total)
                allDone.TrySetResult();
        });

        for (int i = 0; i < total; i++)
            await store.StoreAsync(MakeEntry("proc-batch"), TestContext.Current.CancellationToken);

        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMinutes(1),
            batchSize: 10);

        // Act
        await loop.StartAsync(TestContext.Current.CancellationToken);
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // DisposeAsync cancels the next Task.Delay and awaits the background task,
        // ensuring all in-flight RemoveAsync calls have completed before we assert.
        await loop.DisposeAsync();

        // Assert — all three entries removed from the store
        var remaining = await store.GetAsync("proc-batch");
        Assert.Empty(remaining);
    }

    // ── no-op when retry not requested ───────────────────────────────────────

    [Fact]
    public async Task EntryWithoutRetryRequested_IsNeverClaimed_OrDispatched()
    {
        // Arrange — entry is NOT marked for retry; the loop should ignore it
        var store = new InMemoryDeadLetterStore();
        var processor = new CapturingProcessor("proc-norequested");
        var entry = MakeEntry("proc-norequested", retryRequested: false);
        await store.StoreAsync(entry, TestContext.Current.CancellationToken);

        // Short polling interval so the loop has multiple poll opportunities within the test window.
        var loop = new DeadLetterRetryLoop(
            processor,
            store,
            pollingInterval: TimeSpan.FromMilliseconds(50));

        // Act — run briefly then stop
        await loop.StartAsync(TestContext.Current.CancellationToken);
        // Allow several poll cycles (50 ms each) without triggering a positive result
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        await loop.DisposeAsync();

        // Assert — processor received nothing
        Assert.Empty(processor.Processed);

        // Assert — entry still present and unchanged
        var remaining = await store.GetAsync("proc-norequested");
        var survivor = Assert.Single(remaining);
        Assert.Equal(entry.Id, survivor.Id);
        Assert.False(survivor.RetryRequested);
    }
}
