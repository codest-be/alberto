using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="IClaimableDeadLetterStore"/> implementations —
/// every fact from <see cref="DeadLetterStoreSpecification"/> plus the claim-lease protocol
/// that automatic retry depends on.
///
/// Derive from this class and implement <see cref="CreateClaimableStore"/> to run Alberto's
/// own suite against your implementation. A store that cannot offer an atomic
/// claim-and-fence should derive from <see cref="DeadLetterStoreSpecification"/> instead and
/// not implement <see cref="IClaimableDeadLetterStore"/> at all — a claim that is not atomic
/// hands the same failed event to two workers, which is worse than not retrying it.
/// </summary>
public abstract class ClaimableDeadLetterStoreSpecification : DeadLetterStoreSpecification
{
    /// <summary>
    /// Factory method called once per fact to create the store under test.
    /// </summary>
    protected abstract Task<IClaimableDeadLetterStore> CreateClaimableStore();

    /// <inheritdoc />
    protected override async Task<IDeadLetterStore> CreateStore() => await CreateClaimableStore();

    // ── MarkForRetry + ClaimRetryRequested ────────────────────────────────────

    /// <summary>
    /// Entries marked for retry must become available for claiming via
    /// <c>ClaimRetryRequestedAsync</c>.
    /// </summary>
    [Fact]
    public async Task MarkForRetryAsync_ThenClaimRetryRequested_ReturnsClaim()
    {
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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

    /// <summary>
    /// <c>CompleteRetryAsync</c> must return <see langword="false"/> when the entry
    /// has already been removed — for example, when a previous call already completed
    /// the retry. The interface contract is "returns true when the claimed row was
    /// removed"; if there is no row, the outcome is false.
    /// </summary>
    [Fact]
    public async Task CompleteRetryAsync_WhenEntryAlreadyGone_ReturnsFalse()
    {
        var store = await CreateClaimableStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var claim = Assert.Single(claims);

        // First call removes the entry.
        Assert.True(await store.CompleteRetryAsync(claim, Ct));

        // Second call: entry is gone — must return false, not throw.
        var result = await store.CompleteRetryAsync(claim, Ct);

        Assert.False(result);
    }

    // ── AbandonRetryAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>AbandonRetryAsync</c> with a valid claim must return <see langword="true"/>
    /// and leave the entry in the store (the entry is not deleted, just unclaimed).
    /// </summary>
    [Fact]
    public async Task AbandonRetryAsync_WithValidClaim_ReturnsTrueAndKeepsEntry()
    {
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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
        var store = await CreateClaimableStore();
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

    /// <summary>
    /// <c>AbandonRetryAsync</c> must return <see langword="false"/> when the entry
    /// has already been removed — for example, after a concurrent
    /// <c>CompleteRetryAsync</c> deleted the row. The interface contract is
    /// "returns true when the active claim was abandoned"; if there is no row to
    /// update, the outcome is false.
    /// </summary>
    [Fact]
    public async Task AbandonRetryAsync_WhenEntryAlreadyGone_ReturnsFalse()
    {
        var store = await CreateClaimableStore();
        await store.StoreAsync(NewEntry(ProcessorId), Ct);
        await store.MarkForRetryAsync(ProcessorId, Ct);
        var claims = await store.ClaimRetryRequestedAsync(
            ProcessorId, batchSize: 10, leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "worker", ct: Ct);
        var claim = Assert.Single(claims);

        // Remove the entry via a successful completion.
        Assert.True(await store.CompleteRetryAsync(claim, Ct));

        // Trying to abandon a claim for a row that no longer exists must return false.
        var result = await store.AbandonRetryAsync(claim, Ct);

        Assert.False(result);
    }
}
