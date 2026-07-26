using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="IDeadLetterStore"/> implementations.
///
/// Derive from this class and implement <see cref="CreateStore"/> to run Alberto's own
/// dead-letter store test suite against your implementation.
/// Every fact describes an observable contract that all implementations must satisfy.
/// </summary>
public abstract class DeadLetterStoreSpecification
{
    /// <summary>
    /// Unique prefix generated per test instance to isolate processor IDs across runs.
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    private CancellationToken Ct => TestContext.Current.CancellationToken;

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
    protected static DeadLetterEntry NewEntry(string processorId) => new(
        Id: Guid.NewGuid(),
        ProcessorId: processorId,
        EventId: Guid.NewGuid(),
        EventType: "order-placed",
        EventData: """{"orderId":"test"}""",
        ErrorMessage: "Processing failed in test",
        StackTrace: null,
        AttemptCount: 1,
        FailedAt: DateTimeOffset.UtcNow);

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

    // ── MarkForRetry + ClaimRetryRequested ────────────────────────────────────

    /// <summary>
    /// Entries marked for retry must become available for claiming via
    /// <c>ClaimRetryRequestedAsync</c>.
    /// </summary>
    [Fact]
    public async Task MarkForRetryAsync_ThenClaimRetryRequested_ReturnsClaim()
    {
        var store = await CreateStore();
        var entry = NewEntry(ProcessorId);
        await store.StoreAsync(entry, Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);

        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);

        var claim = Assert.Single(claims);
        Assert.Equal(entry.Id, claim.Entry.Id);
    }

    /// <summary>
    /// Entries that are not marked for retry must not be returned by
    /// <c>ClaimRetryRequestedAsync</c>.
    /// </summary>
    [Fact]
    public async Task ClaimRetryRequestedAsync_IgnoresEntriesNotMarkedForRetry()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        // Intentionally NOT calling MarkForRetryAsync

        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);

        Assert.Empty(claims);
    }

    /// <summary>
    /// A currently-claimed entry must not be returned by a second
    /// <c>ClaimRetryRequestedAsync</c> call while the first lease is still held.
    /// </summary>
    [Fact]
    public async Task ClaimRetryRequestedAsync_HeldLease_IsNotReclaimable()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);

        // First worker claims the entry with a generous lease.
        var first = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromHours(1),
            claimedBy: "worker-1", ct: Ct);
        Assert.Single(first);

        // Second worker must get nothing while the lease is still valid.
        var second = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromHours(1),
            claimedBy: "worker-2", ct: Ct);
        Assert.Empty(second);
    }

    // ── CompleteRetryAsync ────────────────────────────────────────────────────

    /// <summary>
    /// <c>CompleteRetryAsync</c> with a valid claim must return <see langword="true"/>
    /// and remove the entry from the store.
    /// </summary>
    [Fact]
    public async Task CompleteRetryAsync_WithValidClaim_RemovesEntry()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var claim = Assert.Single(claims);

        var result = await store.CompleteRetryAsync(claim, Ct);

        Assert.True(result);
        Assert.Equal(0, await store.CountAsync(ProcessorId, Ct));
    }

    /// <summary>
    /// <c>CompleteRetryAsync</c> with a stale or fabricated token must return
    /// <see langword="false"/> and leave the entry intact.
    /// </summary>
    [Fact]
    public async Task CompleteRetryAsync_WithInvalidToken_ReturnsFalse()
    {
        var store = await CreateStore();
        var entry = NewEntry(ProcessorId);
        await store.StoreAsync(entry, Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var realClaim = Assert.Single(claims);

        // Construct a claim with a wrong token.
        var fabricated = new DeadLetterClaim(realClaim.Entry, Guid.NewGuid(), realClaim.ExpiresAt);
        var result = await store.CompleteRetryAsync(fabricated, Ct);

        Assert.False(result);
        Assert.Equal(1, await store.CountAsync(ProcessorId, Ct));
    }

    // ── AbandonRetryAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>AbandonRetryAsync</c> with a valid claim must return <see langword="true"/>
    /// and leave the entry in the store (the entry is not deleted, just unclaimed).
    /// </summary>
    [Fact]
    public async Task AbandonRetryAsync_WithValidClaim_ReturnsTrueAndKeepsEntry()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var claim = Assert.Single(claims);

        var result = await store.AbandonRetryAsync(claim, Ct);

        Assert.True(result);
        // The entry must still be in the store.
        Assert.Equal(1, await store.CountAsync(ProcessorId, Ct));
    }

    /// <summary>
    /// After <c>AbandonRetryAsync</c>, the entry is unclaimed but is no longer marked for
    /// retry. A subsequent <c>MarkForRetryAsync</c> must be able to re-mark it, after which
    /// a new claim must succeed.
    /// </summary>
    [Fact]
    public async Task AbandonRetryAsync_AfterReMarkForRetry_EntryIsReclaimable()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var first = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-1", ct: Ct);
        await store.AbandonRetryAsync(Assert.Single(first), Ct);

        // Re-mark and re-claim.
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var second = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker-2", ct: Ct);

        Assert.Single(second);
    }

    /// <summary>
    /// <c>AbandonRetryAsync</c> with a stale or fabricated token must return
    /// <see langword="false"/>.
    /// </summary>
    [Fact]
    public async Task AbandonRetryAsync_WithInvalidToken_ReturnsFalse()
    {
        var store = await CreateStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var realClaim = Assert.Single(claims);

        var fabricated = new DeadLetterClaim(realClaim.Entry, Guid.NewGuid(), realClaim.ExpiresAt);
        var result = await store.AbandonRetryAsync(fabricated, Ct);

        Assert.False(result);
    }
}
