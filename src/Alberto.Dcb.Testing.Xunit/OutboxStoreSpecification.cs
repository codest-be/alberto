using Alberto.Dcb.Messaging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Testing.Xunit;

/// <summary>
/// Conformance specification for <see cref="IOutboxStore"/> implementations.
///
/// Derive from this class and implement <see cref="CreateStore"/> and
/// <see cref="TimeProvider"/> to run Alberto's own outbox store test suite against
/// your implementation. Every fact describes an observable contract that all
/// implementations must satisfy.
///
/// <para>
/// Three facts rely on advancing the clock to verify lease-expiry semantics.
/// They call <see cref="Assert.Skip"/> automatically when <see cref="TimeProvider"/>
/// is not a <c>FakeTimeProvider</c> from <c>Microsoft.Extensions.TimeProvider.Testing</c>,
/// so the spec remains runnable against production backends that do not support
/// injected time — those facts simply show as skipped rather than failing.
/// </para>
/// </summary>
public abstract class OutboxStoreSpecification
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Unique prefix generated per test instance.
    /// Used to produce distinct message types in tests that need to isolate their entries
    /// from leftovers in a shared backing store (e.g. a Postgres fixture shared across facts).
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Factory method called once per fact to create the store under test.
    /// </summary>
    protected abstract Task<IOutboxStore> CreateStore();

    /// <summary>
    /// The <see cref="System.TimeProvider"/> that the store uses for claim leases and
    /// delivered timestamps. Supply a <c>FakeTimeProvider</c> for implementations that
    /// accept an injected clock (e.g. <c>InMemoryOutboxStore</c>); supply
    /// <see cref="System.TimeProvider.System"/> for production-backed implementations.
    /// Facts that require controllable time will call <see cref="Assert.Skip"/> when this
    /// is not a <c>FakeTimeProvider</c>.
    /// </summary>
    protected abstract TimeProvider TimeProvider { get; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal <see cref="OutboxEntry"/> with a unique source-event ID.</summary>
    /// <param name="messageType">Message type to assign; defaults to <c>"order-placed"</c>.</param>
    protected static OutboxEntry NewEntry(string messageType = "order-placed") => new(
        Id: Guid.NewGuid(),
        SourceEventId: Guid.NewGuid(),
        MessageType: messageType,
        Version: "1",
        Payload: """{"orderId":"test"}""",
        Metadata: [],
        Status: OutboxEntryStatus.Pending,
        RetryCount: 0,
        LastError: null,
        CreatedAt: DateTimeOffset.UtcNow,
        DeliveredAt: null);

    // ── InsertAsync ───────────────────────────────────────────────────────────

    /// <summary>A freshly inserted entry must start in the <c>Pending</c> status.</summary>
    [Fact]
    public async Task InsertAsync_NewEntry_IsPending()
    {
        var store = await CreateStore();
        var entry = NewEntry();

        await store.InsertAsync(entry, Ct);

        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromHours(1), "worker", Ct);
        // Filter to our specific entry so leftover entries in a shared backing store
        // do not cause spurious failures.
        Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));
    }

    /// <summary>
    /// Inserting an entry whose <c>SourceEventId</c> duplicates an existing entry must be
    /// silently ignored — the duplicate must not appear as a second claimable entry.
    /// </summary>
    [Fact]
    public async Task InsertAsync_DuplicateSourceEvent_IsIgnored()
    {
        var store = await CreateStore();
        var sourceEventId = Guid.NewGuid();
        var first = NewEntry() with { SourceEventId = sourceEventId };
        var second = NewEntry() with { SourceEventId = sourceEventId };

        await store.InsertAsync(first, Ct);
        await store.InsertAsync(second, Ct);

        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromHours(1), "worker", Ct);
        // Among our two inserted entries (sharing a SourceEventId) only the first
        // should be claimable. Filter to our specific IDs to stay resilient against
        // leftover entries in a shared backing store.
        var ours = claims.Where(c => c.Entry.Id == first.Id || c.Entry.Id == second.Id).ToList();
        Assert.Single(ours);
        Assert.Equal(first.Id, ours[0].Entry.Id);
    }

    // ── ClaimPendingAsync ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>ClaimPendingAsync</c> must return an empty list when there are no pending entries
    /// available to claim.
    /// <para>
    /// Any entries already present in the store (e.g. processing entries left by earlier facts
    /// in a shared backing store) are drained first so that a subsequent call exercises the
    /// empty-result path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ClaimPendingAsync_EmptyStore_ReturnsEmpty()
    {
        var store = await CreateStore();

        // Drain any pending entries left by earlier facts in a shared backing store.
        // ClaimPendingAsync marks claimed entries as 'processing', so this call makes
        // all currently-pending entries ineligible for the assertion below.
        await store.ClaimPendingAsync(int.MaxValue, TimeSpan.FromMinutes(5), "drain", Ct);

        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);

        Assert.Empty(claims);
    }

    /// <summary>The <c>limit</c> parameter must cap the number of returned claims.</summary>
    [Fact]
    public async Task ClaimPendingAsync_RespectsLimit()
    {
        var store = await CreateStore();
        await store.InsertAsync(NewEntry(), Ct);
        await store.InsertAsync(NewEntry(), Ct);
        await store.InsertAsync(NewEntry(), Ct);

        var claims = await store.ClaimPendingAsync(2, TimeSpan.FromMinutes(5), "worker", Ct);

        Assert.Equal(2, claims.Count);
    }

    /// <summary>
    /// An entry that is currently claimed with a live lease must not be returned by a
    /// second <c>ClaimPendingAsync</c> call.
    /// </summary>
    [Fact]
    public async Task ClaimPendingAsync_HeldLease_IsNotReclaimable()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);

        // First worker claims with a generous lease.
        var first = await store.ClaimPendingAsync(10, TimeSpan.FromHours(1), "worker-1", Ct);
        Assert.Contains(first, c => c.Entry.Id == entry.Id);

        // Second worker must not be able to reclaim our entry while the lease is still valid.
        var second = await store.ClaimPendingAsync(10, TimeSpan.FromHours(1), "worker-2", Ct);
        Assert.DoesNotContain(second, c => c.Entry.Id == entry.Id);
    }

    /// <summary>
    /// An entry whose lease has expired must become reclaimable by a new worker.
    /// This fact is skipped for implementations that do not support controllable time.
    /// </summary>
    [Fact]
    public async Task ClaimPendingAsync_ExpiredLease_IsReclaimable()
    {
        if (TimeProvider is not FakeTimeProvider)
            Assert.Skip("Requires FakeTimeProvider to control claim expiry.");
        var ftp = (FakeTimeProvider)TimeProvider;

        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);

        // First worker claims with a very short lease.
        await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(10), "worker-1", Ct);

        // Advance past the lease expiry.
        ftp.Advance(TimeSpan.FromSeconds(11));

        // Second worker should now reclaim it.
        var second = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker-2", Ct);
        Assert.Single(second.Where(c => c.Entry.Id == entry.Id));
    }

    // ── MarkDeliveredAsync ────────────────────────────────────────────────────

    /// <summary>
    /// <c>MarkDeliveredAsync</c> with a valid claim must return <see langword="true"/>.
    /// </summary>
    [Fact]
    public async Task MarkDeliveredAsync_WithValidClaim_ReturnsTrue()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        var claim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));

        var result = await store.MarkDeliveredAsync(claim, Ct);

        Assert.True(result);
    }

    /// <summary>
    /// After <c>MarkDeliveredAsync</c> succeeds, the delivered entry must not appear in
    /// a subsequent <c>ClaimPendingAsync</c> call.
    /// </summary>
    [Fact]
    public async Task MarkDeliveredAsync_WithValidClaim_EntryNotReclaimable()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        var ourClaim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));
        await store.MarkDeliveredAsync(ourClaim, Ct);

        var second = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        Assert.DoesNotContain(second, c => c.Entry.Id == entry.Id);
    }

    /// <summary>
    /// <c>MarkDeliveredAsync</c> with a fabricated token must return <see langword="false"/>.
    /// </summary>
    [Fact]
    public async Task MarkDeliveredAsync_WithInvalidToken_ReturnsFalse()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        var realClaim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));

        var fabricated = new OutboxClaim(realClaim.Entry, Guid.NewGuid(), realClaim.ExpiresAt);
        var result = await store.MarkDeliveredAsync(fabricated, Ct);

        Assert.False(result);
    }

    /// <summary>
    /// <c>MarkDeliveredAsync</c> with a valid token after the claim has expired must
    /// return <see langword="false"/>.
    /// This fact is skipped for implementations that do not support controllable time.
    /// </summary>
    [Fact]
    public async Task MarkDeliveredAsync_ExpiredLease_ReturnsFalse()
    {
        if (TimeProvider is not FakeTimeProvider)
            Assert.Skip("Requires FakeTimeProvider to control claim expiry.");
        var ftp = (FakeTimeProvider)TimeProvider;

        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromSeconds(10), "worker", Ct);
        var claim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));

        // Advance past the lease.
        ftp.Advance(TimeSpan.FromSeconds(11));

        var result = await store.MarkDeliveredAsync(claim, Ct);
        Assert.False(result);
    }

    // ── MarkFailedAsync ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>MarkFailedAsync</c> with a valid claim must return <see langword="true"/>.
    /// </summary>
    [Fact]
    public async Task MarkFailedAsync_WithValidClaim_ReturnsTrue()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        var claim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));

        var result = await store.MarkFailedAsync(claim, "delivery error", Ct);

        Assert.True(result);
    }

    /// <summary>
    /// <c>MarkFailedAsync</c> with a fabricated token must return <see langword="false"/>.
    /// </summary>
    [Fact]
    public async Task MarkFailedAsync_WithInvalidToken_ReturnsFalse()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);
        var claims = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(5), "worker", Ct);
        var realClaim = Assert.Single(claims.Where(c => c.Entry.Id == entry.Id));

        var fabricated = new OutboxClaim(realClaim.Entry, Guid.NewGuid(), realClaim.ExpiresAt);
        var result = await store.MarkFailedAsync(fabricated, "delivery error", Ct);

        Assert.False(result);
    }

    // ── RetryFailedAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>RetryFailedAsync</c> must reset failed entries back to pending so they can
    /// be claimed again.
    /// </summary>
    [Fact]
    public async Task RetryFailedAsync_ResetsFailed_ToPending()
    {
        var store = await CreateStore();
        var entry = NewEntry();
        await store.InsertAsync(entry, Ct);

        // Claim the whole pending pool and locate our entry by ID.
        var all = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        var ourClaim = all.FirstOrDefault(c => c.Entry.Id == entry.Id)
            ?? throw new InvalidOperationException($"Inserted entry {entry.Id} was not returned by ClaimPendingAsync.");
        await store.MarkFailedAsync(ourClaim, "error", Ct);

        await store.RetryFailedAsync(ct: Ct);

        var second = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        // Filter to our specific entry so entries from other facts do not inflate the
        // count. Assert.Single (rather than Assert.Contains) additionally catches any
        // backend that duplicates an entry during the failed→pending transition.
        Assert.Single(second.Where(c => c.Entry.Id == entry.Id));
    }

    /// <summary>
    /// <c>RetryFailedAsync</c> with a <c>messageType</c> filter must only reset
    /// failed entries of that type; other failed entries must remain failed.
    ///
    /// <para>
    /// Unique per-test message types (incorporating <see cref="TestId"/>) ensure that
    /// isolation assertions hold even against a shared backing store that may contain
    /// other failed entries with the same generic message types.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RetryFailedAsync_WithMessageTypeFilter_OnlyResetsMatchingType()
    {
        var store = await CreateStore();

        // Use unique message types for this test instance to avoid interference with
        // leftover entries from other facts in a shared backing store.
        var typeA = $"order-placed-{TestId}";
        var typeB = $"payment-captured-{TestId}";

        var entryA = NewEntry(typeA);
        var entryB = NewEntry(typeB);
        await store.InsertAsync(entryA, Ct);
        await store.InsertAsync(entryB, Ct);

        // Claim the whole pending pool and locate our two entries by ID.
        var all = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        var claimA = all.FirstOrDefault(c => c.Entry.Id == entryA.Id)
            ?? throw new InvalidOperationException($"Entry {entryA.Id} not found in claims.");
        var claimB = all.FirstOrDefault(c => c.Entry.Id == entryB.Id)
            ?? throw new InvalidOperationException($"Entry {entryB.Id} not found in claims.");
        await store.MarkFailedAsync(claimA, "error", Ct);
        await store.MarkFailedAsync(claimB, "error", Ct);

        // Reset only typeA.
        await store.RetryFailedAsync(typeA, Ct);

        var second = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        // entryA must be reclaimable (it was reset).
        Assert.Contains(second, c => c.Entry.Id == entryA.Id);
        // entryB must NOT be reclaimable (it was not reset).
        Assert.DoesNotContain(second, c => c.Entry.Id == entryB.Id);
    }

    // ── PurgeDeliveredAsync ───────────────────────────────────────────────────

    /// <summary>
    /// <c>PurgeDeliveredAsync</c> must remove delivered entries created before the
    /// supplied cut-off and leave newer delivered entries and pending entries intact.
    /// </summary>
    [Fact]
    public async Task PurgeDeliveredAsync_RemovesOldDeliveredOnly()
    {
        if (TimeProvider is not FakeTimeProvider)
            Assert.Skip("Requires FakeTimeProvider to produce controllable DeliveredAt timestamps.");
        var ftp = (FakeTimeProvider)TimeProvider;

        var store = await CreateStore();

        // Insert and deliver the first entry.
        var oldEntry = NewEntry();
        await store.InsertAsync(oldEntry, Ct);
        var first = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        var oldClaim = Assert.Single(first.Where(c => c.Entry.Id == oldEntry.Id));
        await store.MarkDeliveredAsync(oldClaim, Ct);

        // Advance time so the second entry gets a later DeliveredAt.
        ftp.Advance(TimeSpan.FromMinutes(10));
        var cutOff = ftp.GetUtcNow();

        var recentEntry = NewEntry();
        await store.InsertAsync(recentEntry, Ct);
        var second = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        var recentClaim = Assert.Single(second.Where(c => c.Entry.Id == recentEntry.Id));
        await store.MarkDeliveredAsync(recentClaim, Ct);

        // Purge everything delivered before the cut-off — only the old entry.
        await store.PurgeDeliveredAsync(cutOff, Ct);

        // The recent delivered entry must survive; a new insertion of the old
        // SourceEventId should succeed (meaning the old entry is gone).
        var reinserted = oldEntry with { Id = Guid.NewGuid() };
        await store.InsertAsync(reinserted, Ct);
        var remaining = await store.ClaimPendingAsync(100, TimeSpan.FromMinutes(5), "worker", Ct);
        // reinserted must be claimable — oldEntry was purged so its SourceEventId dedup is gone.
        Assert.Single(remaining.Where(c => c.Entry.Id == reinserted.Id));
        // recentEntry was delivered but not purged; it must not be claimable.
        Assert.DoesNotContain(remaining, c => c.Entry.Id == recentEntry.Id);
    }
}
