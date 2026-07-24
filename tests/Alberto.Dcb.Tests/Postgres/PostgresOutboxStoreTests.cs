using Alberto.Dcb.Messaging;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Postgres.Messaging;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Fixture that spins up a real PostgreSQL container, runs single-tenant migrations,
/// and then patches the outbox status CHECK constraint to include 'processing'.
/// </summary>
/// <remarks>
/// CROSS-FILE NOTE (crossFileNeeds): The single-tenant migration
/// <c>Migrations/SingleTenant/001_InitialSchema.sql</c> and its multi-tenant
/// counterpart <c>Migrations/001_InitialSchema.sql</c> both define the constraint
/// as <c>CHECK (status IN ('pending', 'delivered', 'failed'))</c>.  The
/// <see cref="PostgresOutboxStore.GetPendingAsync"/> implementation claims rows by
/// updating their status to <c>'processing'</c>, which violates that constraint
/// at runtime.  A new migration must extend the constraint to
/// <c>('pending', 'processing', 'delivered', 'failed')</c> and update the partial
/// index so the planner can use it for 'processing' status too.
/// Until that migration ships this fixture patches the constraint manually so
/// that these tests can run against the current schema.
/// </remarks>
public sealed class PostgresOutboxStoreFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();
        var migrationResult = PostgresMigrator.Migrate(connectionString, singleTenant: true);
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        DataSource = NpgsqlDataSource.Create(connectionString);

        // Patch the CHECK constraint until the migration is updated (see class remarks above).
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE alberto_outbox_entries
                DROP CONSTRAINT IF EXISTS alberto_outbox_entries_status_check;
            ALTER TABLE alberto_outbox_entries
                ADD CONSTRAINT alberto_outbox_entries_status_check
                CHECK (status IN ('pending', 'processing', 'delivered', 'failed'));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

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
        var pending = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var retrieved = pending.FirstOrDefault(e => e.Id == entry.Id);
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

        var pending = await store.GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);
        var rowsForSource = pending.Where(e => e.SourceEventId == entry.SourceEventId).ToList();
        Assert.Single(rowsForSource);
    }

    [Fact]
    public async Task GetPendingAsync_ShouldRespectLimit()
    {
        var store = CreateStore();
        var entries = Enumerable.Range(0, 5).Select(_ => MakeEntry()).ToList();

        foreach (var e in entries)
            await store.InsertAsync(e, TestContext.Current.CancellationToken);

        var batch = await store.GetPendingAsync(limit: 2, TestContext.Current.CancellationToken);

        // At least 2 entries exist; the limit must be honoured.  (The store may
        // contain rows from other tests so we assert <= 2, not == 2.)
        Assert.True(batch.Count <= 2);
    }

    [Fact]
    public async Task GetPendingAsync_ShouldReturnEntriesOrderedByCreatedAt()
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

        var pending = await store.GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);

        // Filter to just the three we inserted (other tests may have left rows).
        var ours = pending
            .Where(e => e.Id == first.Id || e.Id == second.Id || e.Id == third.Id)
            .ToList();

        Assert.Equal(3, ours.Count);
        Assert.Equal(first.Id, ours[0].Id);
        Assert.Equal(second.Id, ours[1].Id);
        Assert.Equal(third.Id, ours[2].Id);
    }

    [Fact]
    public async Task MarkDeliveredAsync_ShouldTransitionStatusAndSetDeliveredAt()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.First(e => e.Id == entry.Id);

        await store.MarkDeliveredAsync(claimedEntry.Id, TestContext.Current.CancellationToken);

        Assert.Equal("delivered", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MarkFailedAsync_ShouldSetErrorAndIncrementRetryCount()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.First(e => e.Id == entry.Id);

        await store.MarkFailedAsync(claimedEntry.Id, "connection timeout", TestContext.Current.CancellationToken);

        Assert.Equal("failed", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));

        // Verify retry_count incremented by reading a fresh pending-only query won't help
        // since the row is failed; use raw SQL instead.
        await using var conn = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT retry_count, last_error FROM alberto_outbox_entries WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", claimedEntry.Id);
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

        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.First(e => e.Id == entry.Id);
        await store.MarkFailedAsync(claimedEntry.Id, "boom", TestContext.Current.CancellationToken);

        await store.RetryFailedAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("pending", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));
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
        var claimed = await store.GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);
        var claimedMatch = claimed.First(e => e.Id == matchEntry.Id);
        var claimedOther = claimed.First(e => e.Id == otherEntry.Id);

        await store.MarkFailedAsync(claimedMatch.Id, "err", TestContext.Current.CancellationToken);
        await store.MarkFailedAsync(claimedOther.Id, "err", TestContext.Current.CancellationToken);

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
        var claimed = await store.GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);
        await store.MarkDeliveredAsync(
            claimed.First(e => e.Id == toDeliver.Id).Id,
            TestContext.Current.CancellationToken);

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
    // P0.4 — Claim / SKIP LOCKED / relay-crash scenarios
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// After <see cref="PostgresOutboxStore.GetPendingAsync"/> claims an entry
    /// (transitions it to 'processing'), a second call from the same or a different
    /// relay instance must not return that entry again.  This guards against the
    /// double-delivery risk described in finding P0.4.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_ClaimedEntry_IsNotReturnedBySubsequentCall()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        // First relay instance claims the entry.
        var firstBatch = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = firstBatch.FirstOrDefault(e => e.Id == entry.Id);
        Assert.NotNull(claimedEntry);

        // The entry is now 'processing'.
        Assert.Equal("processing", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));

        // A second relay instance (separate store instance) must not return the same row.
        var secondBatch = await CreateStore().GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(secondBatch, e => e.Id == claimedEntry.Id);
    }

    /// <summary>
    /// Simulates a relay crash between the moment it published the message and the
    /// moment it called <see cref="PostgresOutboxStore.MarkDeliveredAsync"/>.
    ///
    /// Expected guarantees:
    /// <list type="bullet">
    ///   <item>The entry must not be silently lost — it still exists in the store.</item>
    ///   <item>The entry must not be double-delivered by a concurrent relay — it stays
    ///         in 'processing' state and is invisible to <see cref="PostgresOutboxStore.GetPendingAsync"/>.</item>
    /// </list>
    ///
    /// Operators are expected to reset stuck 'processing' entries via
    /// <see cref="PostgresOutboxStore.RetryFailedAsync"/> after marking them failed,
    /// or via a future timeout-based mechanism.
    /// </summary>
    [Fact]
    public async Task CrashBetweenPublishAndMark_EntryNotLostAndNotDoubleDelivered()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        // The relay claims the entry (simulating "about to publish").
        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.FirstOrDefault(e => e.Id == entry.Id);
        Assert.NotNull(claimedEntry);

        // Crash: the relay dies without calling MarkDeliveredAsync or MarkFailedAsync.
        // (We simply do nothing here, leaving the row in 'processing'.)

        // No lost entry: the row still exists in the store.
        await using var conn = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var existsCmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM alberto_outbox_entries WHERE id = @id)", conn);
        existsCmd.Parameters.AddWithValue("id", claimedEntry.Id);
        var exists = (bool)(await existsCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.True(exists, "Entry was lost from the store after a simulated relay crash.");

        // No double delivery: a fresh relay picking up pending entries does NOT receive
        // the stuck 'processing' entry.
        var concurrentBatch = await CreateStore().GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(concurrentBatch, e => e.Id == claimedEntry.Id);

        // The entry is still in 'processing' state, waiting for operator recovery.
        Assert.Equal("processing", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that two concurrently-running relay instances cannot both claim the
    /// same pending entry.  One relay holds a row-level lock (simulated via a raw
    /// <c>SELECT FOR UPDATE SKIP LOCKED</c> in an open transaction), and the second
    /// relay's <see cref="PostgresOutboxStore.GetPendingAsync"/> call skips that
    /// locked row, picking up only the unlocked entries.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingRelays_DoNotDoubleClaimSameEntry()
    {
        // Seed two entries so we can distinguish which relay gets which.
        var store = CreateStore();
        var entryA = MakeEntry() with { CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var entryB = MakeEntry() with { CreatedAt = DateTimeOffset.UtcNow };

        await store.InsertAsync(entryA, TestContext.Current.CancellationToken);
        await store.InsertAsync(entryB, TestContext.Current.CancellationToken);

        // Relay 1: open a transaction and lock entryA with FOR UPDATE SKIP LOCKED,
        // but do not commit yet.  This simulates relay 1 mid-batch.
        await using var conn1 = await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx1 = await conn1.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using var lockCmd = new NpgsqlCommand(
            "SELECT id FROM alberto_outbox_entries WHERE id = @id FOR UPDATE SKIP LOCKED",
            conn1,
            tx1);
        lockCmd.Parameters.AddWithValue("id", entryA.Id);
        await using var lockReader = await lockCmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        // Drain the reader so the query executes and the lock is held.
        var lockedAny = await lockReader.ReadAsync(TestContext.Current.CancellationToken);
        await lockReader.CloseAsync();

        // entryA is now locked (relay 1 holds the lock).
        Assert.True(lockedAny, "Test setup: relay 1 failed to lock entryA.");

        // Relay 2: call GetPendingAsync — must skip entryA (locked) and claim entryB.
        var relay2Batch = await CreateStore().GetPendingAsync(limit: 100, TestContext.Current.CancellationToken);

        // entryA must NOT appear in relay 2's batch (it was skipped due to the lock).
        Assert.DoesNotContain(relay2Batch, e => e.Id == entryA.Id);

        // entryB must appear in relay 2's batch (it was not locked).
        Assert.Contains(relay2Batch, e => e.Id == entryB.Id);

        // Release relay 1's transaction.
        await tx1.RollbackAsync(TestContext.Current.CancellationToken);

        // entryA must still be in 'pending' state (relay 1 never transitioned it).
        Assert.Equal("pending", await ReadStatusAsync(entryA.Id, TestContext.Current.CancellationToken));

        // entryB must now be 'processing' (relay 2 claimed it).
        Assert.Equal("processing", await ReadStatusAsync(entryB.Id, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// After a relay crash leaves an entry in 'processing', an operator can call
    /// <see cref="PostgresOutboxStore.MarkFailedAsync"/> to record the error and
    /// then <see cref="PostgresOutboxStore.RetryFailedAsync"/> to make the entry
    /// eligible for relay again.  This is the expected operator recovery path.
    /// </summary>
    [Fact]
    public async Task OperatorRecovery_MarkFailedThenRetry_MakesEntryEligibleAgain()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        // Relay claims the entry then crashes.
        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.First(e => e.Id == entry.Id);

        // Operator notices the stuck 'processing' row and marks it failed.
        await store.MarkFailedAsync(claimedEntry.Id, "relay crash", TestContext.Current.CancellationToken);
        Assert.Equal("failed", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));

        // Operator resets it to pending.
        await store.RetryFailedAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("pending", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));

        // The entry is now picked up again by the relay.
        var retryClaimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        Assert.Contains(retryClaimed, e => e.Id == entry.Id);
    }

    /// <summary>
    /// Documents the current behavior of <see cref="PostgresOutboxStore.MarkFailedAsync"/>:
    /// it has no guard on the current status, so it can overwrite a 'delivered' row.
    /// This is harmless via the normal relay path (delivered rows are never in
    /// 'processing' state for SKIP LOCKED to apply), but it is a footgun for direct
    /// callers and operator scripts.
    /// </summary>
    [Fact]
    public async Task MarkFailedAsync_OnDeliveredEntry_OverwritesStatus_DocumentingCurrentBehavior()
    {
        var store = CreateStore();
        var entry = MakeEntry();
        await store.InsertAsync(entry, TestContext.Current.CancellationToken);

        var claimed = await store.GetPendingAsync(limit: 10, TestContext.Current.CancellationToken);
        var claimedEntry = claimed.First(e => e.Id == entry.Id);
        await store.MarkDeliveredAsync(claimedEntry.Id, TestContext.Current.CancellationToken);
        Assert.Equal("delivered", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));

        // Calling MarkFailedAsync on a delivered entry transitions it back to 'failed'.
        // This test documents the current behavior so a future guard is noticed if added.
        await store.MarkFailedAsync(claimedEntry.Id, "should not happen in normal flow", TestContext.Current.CancellationToken);
        Assert.Equal("failed", await ReadStatusAsync(claimedEntry.Id, TestContext.Current.CancellationToken));
    }
}
