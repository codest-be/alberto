using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Testing.Xunit;

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
