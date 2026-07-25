using Alberto.Dcb.Messaging;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Postgres.Messaging;
using Alberto.Dcb.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// A private database on the shared cluster running the shipped single-tenant migrations.
/// No manual schema patching is applied — the tests exercise the schema exactly as it is
/// delivered to production.
/// </summary>
public sealed class PostgresOutboxStoreFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// Integration tests for <see cref="PostgresOutboxStore"/>, focusing on the
/// atomically-claiming <c>FOR UPDATE SKIP LOCKED</c> pattern (P0.4) and the
/// relay crash-between-publish-and-mark scenario.
/// </summary>
public sealed class PostgresOutboxStoreTests(PostgresOutboxStoreFixture fixture)
    : IClassFixture<PostgresOutboxStoreFixture>
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private PostgresOutboxStore CreateStore() => new(fixture.DataSource);

    private static Task<IReadOnlyList<OutboxClaim>> ClaimAsync(
        PostgresOutboxStore store,
        int limit,
        CancellationToken ct) =>
        store.ClaimPendingAsync(
            limit,
            TimeSpan.FromMinutes(5),
            $"test-relay-{Guid.NewGuid():N}",
            ct);

    private static OutboxEntry MakeEntry(string? messageType = null) => new(
        Id: Guid.NewGuid(),
        SourceEventId: Guid.NewGuid(),
        MessageType: messageType ?? "order-created",
        Version: "1",
        Payload: """{"orderId": "test"}""",
        Metadata: new Dictionary<string, string> { ["correlation-id"] = Guid.NewGuid().ToString() },
        Status: OutboxEntryStatus.Pending,
        RetryCount: 0,
        LastError: null,
        CreatedAt: DateTimeOffset.UtcNow,
        DeliveredAt: null);

    private async Task<string> ReadStatusAsync(Guid entryId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM alberto_outbox_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", entryId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string ?? throw new InvalidOperationException($"Entry {entryId} not found.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Basic CRUD
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertAsync_ShouldPersistEntry()
    {
        var store = CreateStore();
        var entry = MakeEntry();

        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        // Round-trip via claim and verify all payload fields survived.
        var pending = await ClaimAsync(store, 10, TestContext.Current.CancellationToken);
        var retrieved = pending.FirstOrDefault(e => e.Entry.Id == entry.Id)?.Entry;
        Assert.NotNull(retrieved);
        Assert.Equal(entry.SourceEventId, retrieved.SourceEventId);
        Assert.Equal(entry.MessageType, retrieved.MessageType);
        Assert.Equal(entry.Version, retrieved.Version);
        Assert.Equal(entry.Payload, retrieved.Payload);
        Assert.Equal(entry.Metadata["correlation-id"], retrieved.Metadata["correlation-id"]);
        Assert.Equal(0, retrieved.RetryCount);
        Assert.Null(retrieved.LastError);
        Assert.Null(retrieved.DeliveredAt);
    }

    [Fact]
    public async Task InsertAsync_DuplicateSourceEventId_ShouldBeIdempotent()
    {
        var store = CreateStore();
        var entry = MakeEntry();

        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        // Second insert with the same SourceEventId must not throw and must not
        // create a second row.
        var duplicate = entry with { Id = Guid.NewGuid() };
        await store.InsertAsync(duplicate, TestContext.Current.CancellationToken);

        var pending = await ClaimAsync(store, 100, TestContext.Current.CancellationToken);
        var rowsForSource = pending.Where(e => e.Entry.SourceEventId == entry.SourceEventId).ToList();
        Assert.Single(rowsForSource);
    }

    [Fact]
    public async Task ClaimPendingAsync_ShouldRespectLimit()
    {
        var store = CreateStore();
        var entries = Enumerable.Range(0, 5).Select(_ => MakeEntry()).ToList();

        foreach (var e in entries)
            await store.InsertAsync(e, TestContext.Current.CancellationToken);

        var batch = await ClaimAsync(store, 2, TestContext.Current.CancellationToken);

        // At least 2 entries exist; the limit must be honoured.  (The store may
        // contain rows from other tests so we assert <= 2, not == 2.)
        Assert.True(batch.Count <= 2);
    }

    [Fact]
    public async Task ClaimPendingAsync_ShouldReturnEntriesOrderedByCreatedAt()
    {
        var store = CreateStore();

        // Insert with distinct, clearly-ordered timestamps.
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var first = MakeEntry() with { CreatedAt = baseTime };
        var second = MakeEntry() with { CreatedAt = baseTime.AddSeconds(1) };
        var third = MakeEntry() with { CreatedAt = baseTime.AddSeconds(2) };

        // Intentionally insert out of order.
        await store.InsertAsync(third, TestContext.Current.CancellationToken);
        await store.InsertAsync(first, TestContext.Current.CancellationToken);
        await store.InsertAsync(second, TestContext.Current.CancellationToken);

        var pending = await ClaimAsync(store, 100, TestContext.Current.CancellationToken);

        // Filter to just the three we inserted (other tests may have left rows).
        var ours = pending
            .Where(e => e.Entry.Id == first.Id || e.Entry.Id == second.Id || e.Entry.Id == third.Id)
            .ToList();

        Assert.Equal(3, ours.Count);
        Assert.Equal(first.Id, ours[0].Entry.Id);
        Assert.Equal(second.Id, ours[1].Entry.Id);
        Assert.Equal(third.Id, ours[2].Entry.Id);
    }

    [Fact]
    public async Task MarkDeliveredAsync_ShouldTransitionStatusAndSetDeliveredAt()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await ClaimAsync(store, 10, TestContext.Current.CancellationToken);
        var claim = claimed.First(e => e.Entry.Id == entry.Id);

        Assert.True(await store.MarkDeliveredAsync(claim, TestContext.Current.CancellationToken));

        Assert.Equal("delivered", await ReadStatusAsync(claim.Entry.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MarkFailedAsync_ShouldSetErrorAndIncrementRetryCount()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await ClaimAsync(store, 10, TestContext.Current.CancellationToken);
        var claim = claimed.First(e => e.Entry.Id == entry.Id);

        Assert.True(await store.MarkFailedAsync(
            claim,
            "connection timeout",
            TestContext.Current.CancellationToken));

        Assert.Equal("failed", await ReadStatusAsync(claim.Entry.Id, TestContext.Current.CancellationToken));

        // Verify retry_count incremented by reading a fresh pending-only query won't help
        // since the row is failed; use raw SQL instead.
        await using var conn = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT retry_count, last_error FROM alberto_outbox_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", claim.Entry.Id);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("connection timeout", reader.GetString(1));
    }

    [Fact]
    public async Task RetryFailedAsync_NoFilter_ShouldResetAllFailedEntriesToPending()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await ClaimAsync(store, 10, TestContext.Current.CancellationToken);
        var claim = claimed.First(e => e.Entry.Id == entry.Id);
        Assert.True(await store.MarkFailedAsync(claim, "boom", TestContext.Current.CancellationToken));

        await store.RetryFailedAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("pending", await ReadStatusAsync(claim.Entry.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetryFailedAsync_WithMessageTypeFilter_ShouldOnlyResetMatchingType()
    {
        var store = CreateStore();
        var matchEntry = MakeEntry("order-cancelled");
        var otherEntry = MakeEntry("order-shipped");

        await store.InsertAsync(matchEntry, TestContext.Current.CancellationToken);
        await store.InsertAsync(otherEntry, TestContext.Current.CancellationToken);

        // Claim both, then mark both as failed.
        var claimed = await ClaimAsync(store, 100, TestContext.Current.CancellationToken);
        var claimedMatch = claimed.First(e => e.Entry.Id == matchEntry.Id);
        var claimedOther = claimed.First(e => e.Entry.Id == otherEntry.Id);

        Assert.True(await store.MarkFailedAsync(claimedMatch, "err", TestContext.Current.CancellationToken));
        Assert.True(await store.MarkFailedAsync(claimedOther, "err", TestContext.Current.CancellationToken));

        await store.RetryFailedAsync(messageType: "order-cancelled", ct: TestContext.Current.CancellationToken);

        Assert.Equal("pending", await ReadStatusAsync(matchEntry.Id, TestContext.Current.CancellationToken));
        Assert.Equal("failed", await ReadStatusAsync(otherEntry.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PurgeDeliveredAsync_ShouldRemoveDeliveredEntriesOlderThanThreshold()
    {
        var store = CreateStore();
        var toDeliver = MakeEntry();
        var toLeaveAsProcessing = MakeEntry();

        await store.InsertAsync(toDeliver, TestContext.Current.CancellationToken);
        await store.InsertAsync(toLeaveAsProcessing, TestContext.Current.CancellationToken);

        // Claim both; mark only one as delivered.  The other remains 'processing'
        // (i.e. not delivered) so it must survive the purge.
        var claimed = await ClaimAsync(store, 100, TestContext.Current.CancellationToken);
        Assert.True(await store.MarkDeliveredAsync(
            claimed.First(e => e.Entry.Id == toDeliver.Id),
            TestContext.Current.CancellationToken));

        // Purge with a threshold one hour in the future so all currently-delivered
        // rows qualify (their delivered_at ≈ now() < now()+1h).
        await store.PurgeDeliveredAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            TestContext.Current.CancellationToken);

        // The delivered entry must be gone.
        await using var conn = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var deliveredCmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM alberto_outbox_entries WHERE id = @id)", conn);
        deliveredCmd.Parameters.AddWithValue("id", toDeliver.Id);
        Assert.False(
            (bool)(await deliveredCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!,
            "Delivered entry was not purged.");

        // The non-delivered entry must still be present.
        await using var processingCmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM alberto_outbox_entries WHERE id = @id)", conn);
        processingCmd.Parameters.AddWithValue("id", toLeaveAsProcessing.Id);
        Assert.True(
            (bool)(await processingCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!,
            "Non-delivered entry was incorrectly purged.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Claim leases and fencing
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimPendingAsync_LiveClaim_IsInvisibleToAnotherRelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, ct);

        var firstClaim = (await ClaimAsync(store, 100, ct))
            .Single(c => c.Entry.Id == entry.Id);

        var secondRelayClaims = await ClaimAsync(CreateStore(), 100, ct);

        Assert.Equal(OutboxEntryStatus.Processing, firstClaim.Entry.Status);
        Assert.True(firstClaim.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.DoesNotContain(secondRelayClaims, c => c.Entry.Id == entry.Id);
    }

    [Fact]
    public async Task ClaimPendingAsync_ExpiredClaim_IsRecoveredWithNewToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, ct);

        var original = (await ClaimAsync(store, 100, ct))
            .Single(c => c.Entry.Id == entry.Id);

        await ExpireClaimAsync(entry.Id, ct);

        var recovered = (await ClaimAsync(CreateStore(), 100, ct))
            .Single(c => c.Entry.Id == entry.Id);

        Assert.NotEqual(original.Token, recovered.Token);
        Assert.True(recovered.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Completion_WithExpiredOrSupersededToken_CannotOverwriteClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, ct);

        var stale = (await ClaimAsync(store, 100, ct))
            .Single(c => c.Entry.Id == entry.Id);
        await ExpireClaimAsync(entry.Id, ct);

        Assert.False(await store.MarkDeliveredAsync(stale, ct));
        Assert.False(await store.MarkFailedAsync(stale, "expired relay", ct));

        var current = (await ClaimAsync(CreateStore(), 100, ct))
            .Single(c => c.Entry.Id == entry.Id);

        Assert.False(await store.MarkDeliveredAsync(stale, ct));
        Assert.False(await store.MarkFailedAsync(stale, "stale relay", ct));
        Assert.Equal("processing", await ReadStatusAsync(entry.Id, ct));

        Assert.True(await store.MarkDeliveredAsync(current, ct));
        Assert.Equal("delivered", await ReadStatusAsync(entry.Id, ct));
    }

    [Fact]
    public async Task ClaimPendingAsync_LegacyProcessingRowWithoutLease_IsRecovered()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, ct);

        await using (var conn = await fixture.DataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE alberto_outbox_entries
            SET status = 'processing',
                claim_id = NULL,
                claimed_by = NULL,
                claim_expires_at = NULL
            WHERE id = @id
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("id", entry.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var recovered = await ClaimAsync(CreateStore(), 100, ct);

        Assert.Contains(recovered, c => c.Entry.Id == entry.Id);
    }

    [Fact]
    public async Task Completion_ClearsClaimMetadata_AndDeliveredEntryCannotBeFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, ct);
        var claim = (await ClaimAsync(store, 100, ct))
            .Single(c => c.Entry.Id == entry.Id);

        Assert.True(await store.MarkDeliveredAsync(claim, ct));
        Assert.False(await store.MarkFailedAsync(claim, "late failure", ct));

        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT status, claim_id, claimed_by, claim_expires_at
            FROM alberto_outbox_entries
            WHERE id = @id
            """,
            conn);
        cmd.Parameters.AddWithValue("id", entry.Id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("delivered", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
    }

    private async Task ExpireClaimAsync(Guid entryId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE alberto_outbox_entries
            SET claim_expires_at = now() - interval '1 second'
            WHERE id = @id
            """,
            conn);
        cmd.Parameters.AddWithValue("id", entryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
