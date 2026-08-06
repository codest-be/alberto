using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="IDeadLetterStore"/> implementations.
///
/// Derive from this class and implement <see cref="CreateStore"/> to run Alberto's own
/// dead-letter store test suite against your implementation.
/// Every fact describes an observable contract that all implementations must satisfy.
///
/// <para>
/// This covers the required surface only. A store that also implements
/// <see cref="IClaimableDeadLetterStore"/> — and so can take part in automatic retry —
/// should derive from <see cref="ClaimableDeadLetterStoreSpecification"/> instead, which
/// adds the claim-lease facts on top of every fact here.
/// </para>
/// </summary>
public abstract class DeadLetterStoreSpecification
{
    /// <summary>
    /// Unique prefix generated per test instance to isolate processor IDs across runs.
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The ambient test cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Capability hooks ─────────────────────────────────────────────────────

    /// <summary>
    /// True when entries whose stored <see cref="DeadLetterEntry.TenantId"/> is
    /// <see langword="null"/> are returned regardless of the <c>tenantId</c> argument
    /// passed to <see cref="IDeadLetterStore.GetAsync"/>.
    ///
    /// <para>
    /// In a single-tenant deployment there is no <c>tenant_id</c> column, so every
    /// entry has a null TenantId and a non-null argument must still return them.
    /// InMemory and single-tenant Postgres: <c>true</c>. Multi-tenant Postgres:
    /// <c>false</c> — <c>NULL = @tenantId</c> is false in SQL, so null-TenantId
    /// rows are not returned when a specific tenant is requested. The contract for
    /// multi-tenant mode is tested by
    /// <see cref="GetAsync_MultiTenantStore_TenantScopedQuery_ExcludesOtherTenantEntries"/>.
    /// </para>
    /// </summary>
    protected virtual bool SupportsNullTenantPassthrough => true;

    /// <summary>
    /// True when the store under test supports multi-tenant filtering via the
    /// <c>tenantId</c> argument on <see cref="IDeadLetterStore.GetAsync"/>: a
    /// non-null <c>tenantId</c> must exclude entries whose stored
    /// <see cref="DeadLetterEntry.TenantId"/> belongs to a different tenant.
    ///
    /// <para>
    /// InMemory: <c>true</c> — TenantId is a first-class field on every entry.
    /// Single-tenant Postgres: <c>false</c> — the schema has no <c>tenant_id</c> column,
    /// so filtering is structurally impossible; the contract for that mode is tested
    /// by <see cref="GetAsync_SingleTenantStore_NonNullTenantId_ReturnsEntries"/>.
    /// Multi-tenant Postgres: <c>true</c>.
    /// </para>
    /// </summary>
    protected virtual bool SupportsMultiTenantFiltering => false;

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Factory method called once per fact to create the store under test.
    /// Each call may return the same or a new instance; the specification uses
    /// a fresh <see cref="ProcessorId"/> per test to isolate entries.
    /// </summary>
    protected abstract Task<IDeadLetterStore> CreateStore();

    /// <summary>Returns a unique processor ID for this test instance.</summary>
    protected string ProcessorId => $"processor-{TestId}";

    /// <summary>Creates a minimal <see cref="DeadLetterEntry"/> for the given processor.</summary>
    /// <param name="processorId">The processor ID to assign to the entry.</param>
    protected static DeadLetterEntry NewEntry(string processorId) => new()
    {
        Id = Guid.NewGuid(),
        ProcessorId = processorId,
        EventId = Guid.NewGuid(),
        EventType = "order-placed",
        EventData = """{"orderId":"test"}""",
        ErrorMessage = "Processing failed in test",
        StackTrace = null,
        AttemptCount = 1,
        FailedAt = DateTimeOffset.UtcNow,
    };

    // ── CountAsync ────────────────────────────────────────────────────────────

    /// <summary>An empty store must return zero for any processor ID.</summary>
    [Fact]
    public async Task CountAsync_EmptyStore_ReturnsZero()
    {
        var store = await CreateStore();

        var count = await store.CountAsync(ProcessorId, Ct);

        Assert.Equal(0, count);
    }

    /// <summary>Storing an entry must increment the count for that processor.</summary>
    [Fact]
    public async Task CountAsync_AfterStore_ReturnsOne()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);

        var count = await store.CountAsync(ProcessorId, Ct);

        Assert.Equal(1, count);
    }

    /// <summary>Counts for different processor IDs must be independent of each other.</summary>
    [Fact]
    public async Task CountAsync_DifferentProcessors_AreIsolated()
    {
        var store = await CreateStore();
        var procA = $"proc-A-{TestId}";
        var procB = $"proc-B-{TestId}";

        await store.StoreAsync(NewEntry(procA), Ct);
        await store.StoreAsync(NewEntry(procA), Ct);

        Assert.Equal(2, await store.CountAsync(procA, Ct));
        Assert.Equal(0, await store.CountAsync(procB, Ct));
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────

    /// <summary>Entries stored under a processor must be returned by <c>GetAsync</c> for that processor.</summary>
    [Fact]
    public async Task GetAsync_ReturnsStoredEntries()
    {
        var store = await CreateStore();
        var entry = NewEntry(ProcessorId);
        await store.StoreAsync(entry, Ct);

        var results = await store.GetAsync(ProcessorId, null, 100, Ct);

        Assert.Single(results);
        Assert.Equal(entry.Id, results[0].Id);
    }

    /// <summary>
    /// Passing a non-null <c>tenantId</c> to a single-tenant store must return entries whose
    /// stored <see cref="DeadLetterEntry.TenantId"/> is <see langword="null"/> — the same entries
    /// that a null tenant argument would return. In a single-tenant deployment entries are stored
    /// without a tenant identifier because there is no tenant column to write to; passing an
    /// argument that filters by it would silently discard every entry, turning a valid operator
    /// inquiry into an empty response. Both adapters agree: Postgres ignores the tenant argument
    /// when in single-tenant mode (no tenant_id column), and InMemory treats a null-TenantId
    /// entry as unscoped and returns it regardless of the argument.
    ///
    /// <para>
    /// Skipped for multi-tenant Postgres, where <c>NULL = @tenantId</c> is false in SQL and
    /// entries stored without a tenant are not returned for a specific tenant query. That is the
    /// correct behaviour in multi-tenant mode; see
    /// <see cref="GetAsync_MultiTenantStore_TenantScopedQuery_ExcludesOtherTenantEntries"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetAsync_SingleTenantStore_NonNullTenantId_ReturnsEntries()
    {
        if (!SupportsNullTenantPassthrough)
            Assert.Skip(
                "Multi-tenant stores apply SQL tenant filtering; NULL = @tenantId is false, " +
                "so entries stored without a tenant are not returned for a specific tenant query. " +
                "That is correct for multi-tenant mode — this fact covers single-tenant semantics only.");

        var store = await CreateStore();

        // NewEntry produces an entry with TenantId = null — the shape used by single-tenant stores.
        var entry = NewEntry(ProcessorId);
        await store.StoreAsync(entry, Ct);

        // A caller that passes a non-null tenantId to a single-tenant store must still see
        // the entry. Filtering it out would make the CLI return nothing on single-tenant
        // deployments while the Postgres implementation returns the full list.
        var results = await store.GetAsync(ProcessorId, "any-tenant-does-not-filter", 100, Ct);

        Assert.Single(results);
        Assert.Equal(entry.Id, results[0].Id);
    }

    /// <summary><c>GetAsync</c> must not return entries belonging to a different processor.</summary>
    [Fact]
    public async Task GetAsync_DoesNotReturnEntriesForOtherProcessor()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry($"other-{TestId}"), Ct);

        var results = await store.GetAsync(ProcessorId, null, 100, Ct);

        Assert.Empty(results);
    }

    /// <summary>The <c>limit</c> parameter must cap the number of returned entries.</summary>
    [Fact]
    public async Task GetAsync_RespectsLimit()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.StoreAsync(NewEntry(ProcessorId), Ct);

        var results = await store.GetAsync(ProcessorId, null, 2, Ct);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// <c>GetAsync</c> must return entries sorted newest-first by
    /// <see cref="DeadLetterEntry.FailedAt"/>. The operator views (CLI, admin) display
    /// most-recent failures at the top; both adapters agree on <c>ORDER BY failed_at DESC</c>.
    /// </summary>
    [Fact]
    public async Task GetAsync_ReturnsEntriesNewestFirst()
    {
        var store = await CreateStore();
        var now = DateTimeOffset.UtcNow;
        var older = NewEntry(ProcessorId) with { FailedAt = now.AddSeconds(-10) };
        var newer = NewEntry(ProcessorId) with { FailedAt = now };

        await store.StoreAsync(older, Ct);
        await store.StoreAsync(newer, Ct);

        var results = await store.GetAsync(ProcessorId, null, 100, Ct);

        Assert.Equal(2, results.Count);
        Assert.Equal(newer.Id, results[0].Id);
        Assert.Equal(older.Id, results[1].Id);
    }

    /// <summary>
    /// In a multi-tenant store, a query scoped to tenant A must not return entries
    /// whose <see cref="DeadLetterEntry.TenantId"/> belongs to tenant B.
    /// Dead letter entries carry the full event payload; leaking one tenant's data to
    /// another tenant's query would be a data-isolation failure.
    ///
    /// <para>
    /// Skipped for adapters where <see cref="SupportsMultiTenantFiltering"/> is
    /// <see langword="false"/> (e.g. single-tenant Postgres, where the schema has no
    /// <c>tenant_id</c> column and the <c>tenantId</c> argument is always a no-op).
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetAsync_MultiTenantStore_TenantScopedQuery_ExcludesOtherTenantEntries()
    {
        if (!SupportsMultiTenantFiltering)
            Assert.Skip(
                "This adapter does not support multi-tenant filtering. " +
                "In single-tenant mode the tenantId argument is a no-op by contract — " +
                "see GetAsync_SingleTenantStore_NonNullTenantId_ReturnsEntries for that path. " +
                "Only InMemory and multi-tenant Postgres exercise this fact.");

        var store = await CreateStore();
        var entryA = NewEntry(ProcessorId) with { TenantId = $"tenant-A-{TestId}" };
        var entryB = NewEntry(ProcessorId) with { TenantId = $"tenant-B-{TestId}" };

        await store.StoreAsync(entryA, Ct);
        await store.StoreAsync(entryB, Ct);

        var results = await store.GetAsync(ProcessorId, entryA.TenantId, 100, Ct);

        Assert.Single(results);
        Assert.Equal(entryA.Id, results[0].Id);
    }

    // ── ClearAsync ────────────────────────────────────────────────────────────

    /// <summary><c>ClearAsync</c> must remove all entries for the processor so the count returns zero.</summary>
    [Fact]
    public async Task ClearAsync_RemovesAllEntriesForProcessor()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.StoreAsync(NewEntry(ProcessorId), Ct);

        await store.ClearAsync(ProcessorId, Ct);

        Assert.Equal(0, await store.CountAsync(ProcessorId, Ct));
    }

    /// <summary><c>ClearAsync</c> must not remove entries for other processors.</summary>
    [Fact]
    public async Task ClearAsync_DoesNotAffectOtherProcessors()
    {
        var store = await CreateStore();
        var other = $"other-{TestId}";
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.StoreAsync(NewEntry(other), Ct);

        await store.ClearAsync(ProcessorId, Ct);

        Assert.Equal(1, await store.CountAsync(other, Ct));
    }

    /// <summary><c>ClearAsync</c> on an empty processor must not throw.</summary>
    [Fact]
    public async Task ClearAsync_OnEmptyProcessor_DoesNotThrow()
    {
        var store = await CreateStore();

        // Invoking ClearAsync on a processor with no entries must complete without throwing.
        // xUnit treats any uncaught exception as a test failure, so no explicit assertion is needed.
        await store.ClearAsync(ProcessorId, Ct);
    }
}
