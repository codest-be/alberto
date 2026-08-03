using System.Text.Json;
using Alberto.Admin;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

// ---------------------------------------------------------------------------
// Fixtures — one per schema variant needed by the operator tests.
// ---------------------------------------------------------------------------

/// <summary>
/// A private database running the shipped single-tenant migrations.
/// Used for checkpoint, dead-letter, retry-by-rewind, and rebuild operations.
/// </summary>
public sealed class PostgresAdminOperatorSingleTenantFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// A private database running the shipped multi-tenant migrations.
/// <c>alberto_tenant_leases</c> only exists in multi-tenant schemas.
/// </summary>
public sealed class PostgresAdminOperatorMultiTenantFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant);

// ---------------------------------------------------------------------------
// Single-tenant operator tests: checkpoints, dead letters, retry-by-rewind, rebuilds
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests for <see cref="PostgresAdminOperator"/> methods that work against a
/// single-tenant store. Every mutation asserts both the row-level effect and that the
/// expected admin audit event was appended to <c>alberto_events</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresAdminOperatorSingleTenantTests(PostgresAdminOperatorSingleTenantFixture fixture)
    : IClassFixture<PostgresAdminOperatorSingleTenantFixture>
{
    private PostgresAdminOperator CreateOperator() => new(fixture.DataSource);
    private PostgresCheckpointStore CreateCheckpointStore() => new(fixture.DataSource);

    private async Task InsertDeadLetterAsync(string processorId, long globalPosition, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_dead_letter_events
                (id, processor_id, event_id, event_type, event_data, global_position,
                 error_message, attempt_count, failed_at)
            VALUES
                (@id, @processorId, @eventId, 'test-event', '{}'::jsonb, @globalPosition,
                 'boom', 4, now())
            """,
            conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("eventId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("globalPosition", globalPosition);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the number of a processor's dead letters in the given retry-requested state.
    /// Used to assert that flagging for retry moved the flag and left the rows in place.
    /// </summary>
    private async Task<long> CountDeadLettersAsync(string processorId, bool retryRequested, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM alberto_dead_letter_events
            WHERE processor_id = @proc AND retry_requested = @flag
            """,
            conn);
        cmd.Parameters.AddWithValue("proc", processorId);
        cmd.Parameters.AddWithValue("flag", retryRequested);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Returns the total number of events in the log. Used to assert that no audit event
    /// was appended when a guard clause prevents it.
    /// </summary>
    private async Task<long> CountEventsAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM alberto_events", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Returns the most recently appended event's type and deserialized JSON payload.
    /// </summary>
    private async Task<(string EventType, JsonDocument EventData)> ReadLastEventAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT event_type, event_data FROM alberto_events ORDER BY global_position DESC LIMIT 1",
            conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), "Expected at least one event in the log");
        var eventType = reader.GetString(0);
        var eventData = JsonDocument.Parse(reader.GetString(1));
        return (eventType, eventData);
    }

    // -----------------------------------------------------------------------
    // SetCheckpointAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SetCheckpointAsync_NewProcessor_CreatesRowAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var op = CreateOperator();
        var checkpoints = CreateCheckpointStore();

        await op.SetCheckpointAsync(processorId, 42, "test-operator", ct);

        // Row-level effect
        Assert.Equal(42, await checkpoints.GetAsync(processorId, ct));

        // Audit event
        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-checkpoint-rewound", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal(0, data.RootElement.GetProperty("FromPosition").GetInt64());
        Assert.Equal(42, data.RootElement.GetProperty("ToPosition").GetInt64());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task SetCheckpointAsync_ExistingProcessor_OverridesPositionAndCapturesDelta()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(processorId, 100, ct);

        await CreateOperator().SetCheckpointAsync(processorId, 50, "test-operator", ct);

        // Row-level effect — unconditional set, no GREATEST guard
        Assert.Equal(50, await checkpoints.GetAsync(processorId, ct));

        // Audit event captures the from→to delta
        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-checkpoint-rewound", eventType);
        Assert.Equal(100, data.RootElement.GetProperty("FromPosition").GetInt64());
        Assert.Equal(50, data.RootElement.GetProperty("ToPosition").GetInt64());
    }

    // -----------------------------------------------------------------------
    // RenameCheckpointAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RenameCheckpointAsync_MovesPositionAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"proc-{Guid.NewGuid():N}";
        var to = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(from, 77, ct);

        var result = await CreateOperator().RenameCheckpointAsync(from, to, "test-operator", ct);

        Assert.Equal(CheckpointRenameStatus.Renamed, result.Status);
        Assert.Equal(77, result.Position);

        // Row-level effect: the position moved, the source is gone
        Assert.Equal(77, await checkpoints.GetAsync(to, ct));
        Assert.Null(await checkpoints.GetAsync(from, ct));

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-checkpoint-renamed", eventType);
        Assert.Equal(from, data.RootElement.GetProperty("FromProcessorId").GetString());
        Assert.Equal(to, data.RootElement.GetProperty("ToProcessorId").GetString());
        Assert.Equal(77, data.RootElement.GetProperty("Position").GetInt64());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task RenameCheckpointAsync_DestinationTaken_RefusesAndLeavesBothInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"proc-{Guid.NewGuid():N}";
        var to = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(from, 10, ct);
        await checkpoints.SaveAsync(to, 20, ct);
        var eventCountBefore = await CountEventsAsync(ct);

        var result = await CreateOperator().RenameCheckpointAsync(from, to, "test-operator", ct);

        Assert.Equal(CheckpointRenameStatus.DestinationExists, result.Status);
        // The destination's own position is reported, not the source's — an operator needs to
        // know what is already there to decide what to do about it.
        Assert.Equal(20, result.Position);

        // Neither side moved, and nothing was recorded: the refusal is not an operation.
        Assert.Equal(10, await checkpoints.GetAsync(from, ct));
        Assert.Equal(20, await checkpoints.GetAsync(to, ct));
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_SourceMissing_ReturnsSourceNotFoundAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"proc-{Guid.NewGuid():N}";
        var to = $"proc-{Guid.NewGuid():N}";
        var eventCountBefore = await CountEventsAsync(ct);

        var result = await CreateOperator().RenameCheckpointAsync(from, to, "test-operator", ct);

        Assert.Equal(CheckpointRenameStatus.SourceNotFound, result.Status);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // ResetCheckpointAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResetCheckpointAsync_ExistingCheckpoint_DeletesRowAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(processorId, 77, ct);

        await CreateOperator().ResetCheckpointAsync(processorId, "test-operator", ct);

        // Row-level effect
        Assert.Null(await checkpoints.GetAsync(processorId, ct));

        // Audit event
        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-checkpoint-reset", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task ResetCheckpointAsync_NonExistentProcessor_StillAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";

        await CreateOperator().ResetCheckpointAsync(processorId, "test-operator", ct);

        // Audit event is unconditional — no deletedCount guard
        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-checkpoint-reset", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
    }

    /// <summary>
    /// Inserts one outbox entry in the given state. <paramref name="deliveredAt"/> is what the
    /// retention cutoff is compared against; a <c>pending</c> or <c>failed</c> entry leaves it null.
    /// </summary>
    private async Task InsertOutboxEntryAsync(
        string status, DateTimeOffset? deliveredAt, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_outbox_entries
                (id, source_event_id, message_type, version, payload, status, delivered_at)
            VALUES
                (@id, @sourceEventId, 'test-message', '1', '{}'::jsonb, @status, @deliveredAt)
            """,
            conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("sourceEventId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("deliveredAt", (object?)deliveredAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> CountOutboxEntriesAsync(string status, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM alberto_outbox_entries WHERE status = @status", conn);
        cmd.Parameters.AddWithValue("status", status);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // -----------------------------------------------------------------------
    // PurgeOutboxAsync
    // -----------------------------------------------------------------------

    /// <summary>
    /// Only <c>delivered</c> entries older than the cutoff go. A recently delivered one stays
    /// because of the cutoff; <c>pending</c>, <c>processing</c> and <c>failed</c> stay whatever
    /// their age, because they are work rather than history.
    /// </summary>
    [Fact]
    public async Task PurgeOutboxAsync_RemovesOnlyOldDeliveredEntriesAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromDays(7);

        // Clear whatever earlier tests sharing this fixture left behind.
        await CreateOperator().PurgeOutboxAsync(now, "flush", ct);

        await InsertOutboxEntryAsync("delivered", now - TimeSpan.FromDays(30), ct);
        await InsertOutboxEntryAsync("delivered", now - TimeSpan.FromDays(8), ct);
        await InsertOutboxEntryAsync("delivered", now - TimeSpan.FromDays(1), ct);
        await InsertOutboxEntryAsync("pending", null, ct);
        await InsertOutboxEntryAsync("processing", null, ct);
        await InsertOutboxEntryAsync("failed", null, ct);

        var deleted = await CreateOperator().PurgeOutboxAsync(cutoff, "test-operator", ct);

        Assert.Equal(2, deleted);
        Assert.Equal(1, await CountOutboxEntriesAsync("delivered", ct));
        Assert.Equal(1, await CountOutboxEntriesAsync("pending", ct));
        Assert.Equal(1, await CountOutboxEntriesAsync("processing", ct));
        Assert.Equal(1, await CountOutboxEntriesAsync("failed", ct));

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-outbox-purged", eventType);
        Assert.Equal(2, data.RootElement.GetProperty("DeletedCount").GetInt32());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task PurgeOutboxAsync_NothingToPurge_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        await CreateOperator().PurgeOutboxAsync(now, "flush", ct);

        var eventCountBefore = await CountEventsAsync(ct);

        var deleted = await CreateOperator().PurgeOutboxAsync(now, "test-operator", ct);

        Assert.Equal(0, deleted);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // ClearAllDeadLettersAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ClearAllDeadLettersAsync_WithDeadLetters_ClearsAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Flush any dead letters left by other tests sharing this fixture.
        await CreateOperator().ClearAllDeadLettersAsync("flush", ct);

        var procA = $"proc-{Guid.NewGuid():N}";
        var procB = $"proc-{Guid.NewGuid():N}";
        await InsertDeadLetterAsync(procA, 10, ct);
        await InsertDeadLetterAsync(procA, 20, ct);
        await InsertDeadLetterAsync(procB, 30, ct);

        var count = await CreateOperator().ClearAllDeadLettersAsync("test-operator", ct);

        Assert.Equal(3, count);

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-dead-letters-cleared", eventType);
        Assert.Equal(3, data.RootElement.GetProperty("DeletedCount").GetInt32());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task ClearAllDeadLettersAsync_EmptyTable_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Flush any dead letters left by other tests sharing this fixture.
        await CreateOperator().ClearAllDeadLettersAsync("flush", ct);

        var eventCountBefore = await CountEventsAsync(ct);

        var count = await CreateOperator().ClearAllDeadLettersAsync("test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // ClearDeadLettersForProcessorAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ClearDeadLettersForProcessorAsync_ClearsOnlyTargetAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = $"proc-{Guid.NewGuid():N}";
        var other = $"proc-{Guid.NewGuid():N}";
        await InsertDeadLetterAsync(target, 10, ct);
        await InsertDeadLetterAsync(target, 20, ct);
        await InsertDeadLetterAsync(other, 30, ct);

        var count = await CreateOperator().ClearDeadLettersForProcessorAsync(target, "test-operator", ct);

        Assert.Equal(2, count);

        // The other processor's dead letter survives
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM alberto_dead_letter_events WHERE processor_id = @proc", conn);
        cmd.Parameters.AddWithValue("proc", other);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(ct))!);

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-dead-letters-cleared", eventType);
        Assert.Equal(2, data.RootElement.GetProperty("DeletedCount").GetInt32());
    }

    [Fact]
    public async Task ClearDeadLettersForProcessorAsync_NoDeadLetters_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var eventCountBefore = await CountEventsAsync(ct);

        var count = await CreateOperator().ClearDeadLettersForProcessorAsync(processorId, "test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // MarkDeadLettersForRetryAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MarkDeadLettersForRetryAsync_FlagsOnlyTargetAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = $"proc-{Guid.NewGuid():N}";
        var other = $"proc-{Guid.NewGuid():N}";
        await InsertDeadLetterAsync(target, 10, ct);
        await InsertDeadLetterAsync(target, 20, ct);
        await InsertDeadLetterAsync(other, 30, ct);

        var count = await CreateOperator().MarkDeadLettersForRetryAsync(target, "test-operator", ct);

        Assert.Equal(2, count);

        // The rows stay put — this flags them, it does not delete them or move a checkpoint.
        Assert.Equal(2L, await CountDeadLettersAsync(target, retryRequested: true, ct));
        Assert.Equal(0L, await CountDeadLettersAsync(other, retryRequested: true, ct));

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-dead-letters-marked-for-retry", eventType);
        Assert.Equal(target, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("MarkedCount").GetInt32());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task MarkDeadLettersForRetryAsync_AlreadyFlagged_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        await InsertDeadLetterAsync(processorId, 10, ct);
        await CreateOperator().MarkDeadLettersForRetryAsync(processorId, "test-operator", ct);
        var eventCountBefore = await CountEventsAsync(ct);

        // A second call finds nothing left to flag: the count is rows this call changed, not
        // rows already waiting for the retry loop.
        var count = await CreateOperator().MarkDeadLettersForRetryAsync(processorId, "test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    [Fact]
    public async Task MarkDeadLettersForRetryAsync_NoDeadLetters_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var eventCountBefore = await CountEventsAsync(ct);

        var count = await CreateOperator().MarkDeadLettersForRetryAsync(processorId, "test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // RetryByRewindAsync (operator version — includes audit event)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RetryByRewindAsync_HappyPath_RewindsAndClearsAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        await checkpoints.SaveAsync(processorId, 100, ct);
        await InsertDeadLetterAsync(processorId, 42, ct);
        await InsertDeadLetterAsync(processorId, 57, ct);

        var result = await CreateOperator().RetryByRewindAsync(processorId, "test-operator", ct);

        Assert.Equal(41, result.RewindPosition);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(41, await checkpoints.GetAsync(processorId, ct));

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-dead-letters-retried", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal(41, data.RootElement.GetProperty("RewindPosition").GetInt64());
        Assert.Equal(2, data.RootElement.GetProperty("DeletedCount").GetInt32());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task RetryByRewindAsync_NoDeadLetters_ReturnsNullAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(processorId, 100, ct);

        var eventCountBefore = await CountEventsAsync(ct);

        var result = await CreateOperator().RetryByRewindAsync(processorId, "test-operator", ct);

        Assert.Null(result.RewindPosition);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(100, await checkpoints.GetAsync(processorId, ct));
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    [Fact]
    public async Task RetryByRewindAsync_NoCheckpointRow_CreatesCheckpointAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        await InsertDeadLetterAsync(processorId, 7, ct);

        var result = await CreateOperator().RetryByRewindAsync(processorId, "test-operator", ct);

        Assert.Equal(6, result.RewindPosition);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(6, await checkpoints.GetAsync(processorId, ct));

        var (eventType, _) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-dead-letters-retried", eventType);
    }

    // -----------------------------------------------------------------------
    // Rebuild operations — StartRebuildAsync, PromoteRebuildAsync, AbortRebuildAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartRebuildAsync_StartsRebuildAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var projectionType = $"type-{Guid.NewGuid():N}";

        var result = await CreateOperator().StartRebuildAsync(
            processorId, projectionType, 500, "test-operator", ct);

        Assert.Equal(1, result.ActiveVersion);
        Assert.Equal(2, result.RebuildingVersion);
        Assert.Equal(500, result.TargetPosition);

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-rebuild-started", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal(projectionType, data.RootElement.GetProperty("ProjectionType").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("RebuildingVersion").GetInt32());
        Assert.Equal(500, data.RootElement.GetProperty("TargetPosition").GetInt64());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task PromoteRebuildAsync_RequestsPromotionAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var projectionType = $"type-{Guid.NewGuid():N}";

        // Start a rebuild first
        await CreateOperator().StartRebuildAsync(
            processorId, projectionType, 500, "setup", ct);

        var result = await CreateOperator().PromoteRebuildAsync(
            processorId, force: true, "test-operator", ct);

        Assert.Equal(processorId, result.ProcessorId);
        // Status comes from the state machine — the operator records intent (Rebuilding + ForcePromote action)
        Assert.NotEmpty(result.Status);

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-rebuild-promoted", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task PromoteRebuildAsync_ForceTrue_RecordsForcePromoteStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var projectionType = $"type-{Guid.NewGuid():N}";

        await CreateOperator().StartRebuildAsync(
            processorId, projectionType, 1000, "setup", ct);

        var result = await CreateOperator().PromoteRebuildAsync(
            processorId, force: true, "op", ct);

        // The status reflects the state after the promotion request
        var (_, data) = await ReadLastEventAsync(ct);
        var status = data.RootElement.GetProperty("Status").GetString();
        Assert.NotNull(status);
        Assert.NotEmpty(status);
    }

    [Fact]
    public async Task PromoteRebuildAsync_ForceFalse_OnRebuildingState_RecordsPromoteStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var projectionType = $"type-{Guid.NewGuid():N}";

        await CreateOperator().StartRebuildAsync(
            processorId, projectionType, 1000, "setup", ct);

        // Request non-force promotion while still rebuilding — records intent
        var result = await CreateOperator().PromoteRebuildAsync(
            processorId, force: false, "op", ct);

        Assert.Equal(processorId, result.ProcessorId);

        var (eventType, _) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-rebuild-promoted", eventType);
    }

    [Fact]
    public async Task AbortRebuildAsync_RequestsAbortAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var projectionType = $"type-{Guid.NewGuid():N}";

        await CreateOperator().StartRebuildAsync(
            processorId, projectionType, 500, "setup", ct);

        var result = await CreateOperator().AbortRebuildAsync(processorId, "test-operator", ct);

        Assert.Equal(processorId, result.ProcessorId);
        Assert.NotEmpty(result.Status);

        var (eventType, data) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-rebuild-aborted", eventType);
        Assert.Equal(processorId, data.RootElement.GetProperty("ProcessorId").GetString());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task AbortRebuildAsync_NoRebuildInFlight_ThrowsRebuildStateException()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<RebuildStateException>(
            () => CreateOperator().AbortRebuildAsync(processorId, "op", ct));
    }

    [Fact]
    public async Task PromoteRebuildAsync_NoRebuildInFlight_ThrowsRebuildStateException()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<RebuildStateException>(
            () => CreateOperator().PromoteRebuildAsync(processorId, force: false, "op", ct));
    }
}

// ---------------------------------------------------------------------------
// Multi-tenant operator tests: tenant leases and multi-tenant audit events
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests for <see cref="PostgresAdminOperator.ReleaseTenantLeasesAsync"/> and
/// multi-tenant audit event paths. Uses a multi-tenant database because
/// <c>alberto_tenant_leases</c> only exists in that schema.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresAdminOperatorMultiTenantTests(PostgresAdminOperatorMultiTenantFixture fixture)
    : IClassFixture<PostgresAdminOperatorMultiTenantFixture>
{
    private PostgresAdminOperator CreateOperator() => new(fixture.DataSource);

    /// <summary>
    /// Inserts a tenant lease row. The table only exists in multi-tenant schemas.
    /// </summary>
    private async Task InsertTenantLeaseAsync(
        string consumerId, string tenantId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_tenant_leases
                (consumer_id, tenant_id, replica_id, acquired_at, expires_at)
            VALUES
                (@consumerId, @tenantId, @replicaId, now(), now() + interval '5 minutes')
            """,
            conn);
        cmd.Parameters.AddWithValue("consumerId", consumerId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("replicaId", $"replica-{Guid.NewGuid():N}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> CountEventsAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM alberto_events", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<(string EventType, JsonDocument EventData, string? TenantId)> ReadLastEventAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT event_type, event_data, tenant_id FROM alberto_events ORDER BY global_position DESC LIMIT 1",
            conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct), "Expected at least one event in the log");
        var eventType = reader.GetString(0);
        var eventData = JsonDocument.Parse(reader.GetString(1));
        var tenantId = reader.IsDBNull(2) ? null : reader.GetString(2);
        return (eventType, eventData, tenantId);
    }

    private async Task<long> CountTenantLeasesAsync(string? consumerId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        NpgsqlCommand cmd;
        if (consumerId is not null)
        {
            cmd = new NpgsqlCommand(
                "SELECT count(*) FROM alberto_tenant_leases WHERE consumer_id = @cid", conn);
            cmd.Parameters.AddWithValue("cid", consumerId);
        }
        else
        {
            cmd = new NpgsqlCommand("SELECT count(*) FROM alberto_tenant_leases", conn);
        }

        await using (cmd)
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
    }

    // -----------------------------------------------------------------------
    // ReleaseTenantLeasesAsync — the method whose null-consumerId path shipped
    // a bug (42P08 from untyped NULL parameter). Both paths are tested.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReleaseTenantLeasesAsync_NullConsumerId_ReleasesAllLeasesAndAppendsAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Flush any leases left by other tests sharing this fixture. Releasing with a
        // null consumerId is by definition not scoped, so this test counts every row in
        // the table — including the one ReleaseTenantLeasesAsync_SpecificConsumerId
        // deliberately leaves behind. Without the flush the expected count depends on
        // which tests ran first.
        await CreateOperator().ReleaseTenantLeasesAsync(null, "flush", ct);

        var consumerA = $"consumer-{Guid.NewGuid():N}";
        var consumerB = $"consumer-{Guid.NewGuid():N}";
        await InsertTenantLeaseAsync(consumerA, "tenant-1", ct);
        await InsertTenantLeaseAsync(consumerA, "tenant-2", ct);
        await InsertTenantLeaseAsync(consumerB, "tenant-3", ct);

        // consumerId: null → release ALL leases. This is the fixed bug path.
        var count = await CreateOperator().ReleaseTenantLeasesAsync(null, "test-operator", ct);

        Assert.Equal(3, count);
        Assert.Equal(0, await CountTenantLeasesAsync(null, ct));

        var (eventType, data, tenantId) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-tenant-leases-released", eventType);
        Assert.Equal("__admin__", tenantId); // multi-tenant stores use AdminTenantId
        Assert.True(data.RootElement.GetProperty("ConsumerId").ValueKind == JsonValueKind.Null);
        Assert.Equal(3, data.RootElement.GetProperty("ReleasedCount").GetInt32());
        Assert.Equal("test-operator", data.RootElement.GetProperty("OperatorId").GetString());
    }

    [Fact]
    public async Task ReleaseTenantLeasesAsync_SpecificConsumerId_ReleasesOnlyThatConsumersLeases()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = $"consumer-{Guid.NewGuid():N}";
        var other = $"consumer-{Guid.NewGuid():N}";
        await InsertTenantLeaseAsync(target, "tenant-1", ct);
        await InsertTenantLeaseAsync(target, "tenant-2", ct);
        await InsertTenantLeaseAsync(other, "tenant-3", ct);

        var count = await CreateOperator().ReleaseTenantLeasesAsync(target, "test-operator", ct);

        Assert.Equal(2, count);
        Assert.Equal(0, await CountTenantLeasesAsync(target, ct));
        Assert.Equal(1, await CountTenantLeasesAsync(other, ct));

        var (eventType, data, _) = await ReadLastEventAsync(ct);
        Assert.Equal("admin-tenant-leases-released", eventType);
        Assert.Equal(target, data.RootElement.GetProperty("ConsumerId").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("ReleasedCount").GetInt32());
    }

    [Fact]
    public async Task ReleaseTenantLeasesAsync_NoMatchingLeases_ReturnsZeroAndNoAuditEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var consumerId = $"consumer-{Guid.NewGuid():N}";
        var eventCountBefore = await CountEventsAsync(ct);

        var count = await CreateOperator().ReleaseTenantLeasesAsync(consumerId, "test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    [Fact]
    public async Task ReleaseTenantLeasesAsync_NullConsumerId_NoLeases_ReturnsZeroAndNoAuditEvent()
    {
        // The null path that hit 42P08 — verify it is safe even when no rows match.
        var ct = TestContext.Current.CancellationToken;

        // Flush any leases left by other tests sharing this fixture.
        await CreateOperator().ReleaseTenantLeasesAsync(null, "flush", ct);

        var eventCountBefore = await CountEventsAsync(ct);

        var count = await CreateOperator().ReleaseTenantLeasesAsync(null, "test-operator", ct);

        Assert.Equal(0, count);
        Assert.Equal(eventCountBefore, await CountEventsAsync(ct));
    }

    // -----------------------------------------------------------------------
    // Multi-tenant audit event shape — verify tenant_id and inverted indexes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultiTenant_AuditEvent_UsesAdminTenantIdAndWritesInvertedIndexes()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";

        // Use SetCheckpointAsync as a representative mutation on the multi-tenant store
        await CreateOperator().SetCheckpointAsync(processorId, 10, "test-operator", ct);

        // Verify tenant_id on the audit event
        var (_, _, tenantId) = await ReadLastEventAsync(ct);
        Assert.Equal("__admin__", tenantId);

        // Verify inverted indexes were written
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);

        await using var typeCmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM alberto_event_type_positions
            WHERE event_type = 'admin-checkpoint-rewound' AND tenant_id = '__admin__'
            """,
            conn);
        var typeCount = (long)(await typeCmd.ExecuteScalarAsync(ct))!;
        Assert.True(typeCount >= 1);

        await using var tagCmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM alberto_event_tag_positions
            WHERE tag = @tag AND tenant_id = '__admin__'
            """,
            conn);
        tagCmd.Parameters.AddWithValue("tag", $"admin-checkpoint:{processorId}");
        var tagCount = (long)(await tagCmd.ExecuteScalarAsync(ct))!;
        Assert.True(tagCount >= 1);
    }
}
