using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of dead letter storage.
/// </summary>
public sealed class PostgresDeadLetterStore(NpgsqlDataSource dataSource, string? schema = null) : IDeadLetterStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly SchemaQualifier _schema = new(schema);
    private string SchemaName => _schema.HasSchema ? _schema.Prefix.TrimEnd('.') : "public";

    /// <inheritdoc />
    public async Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO {_schema.Table("alberto_dead_letter_events")} (id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested)
            VALUES (@id, @processorId, @eventId, @eventType, @eventData::jsonb, @errorMessage, @stackTrace, @attemptCount, @failedAt, @globalPosition, FALSE)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("processorId", entry.ProcessorId);
        cmd.Parameters.AddWithValue("eventId", entry.EventId);
        cmd.Parameters.AddWithValue("eventType", entry.EventType);
        cmd.Parameters.AddWithValue("eventData", entry.EventData);
        cmd.Parameters.AddWithValue("errorMessage", entry.ErrorMessage);
        cmd.Parameters.AddWithValue("stackTrace", (object?)entry.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("attemptCount", entry.AttemptCount);
        cmd.Parameters.AddWithValue("failedAt", entry.FailedAt);
        cmd.Parameters.AddWithValue("globalPosition", entry.GlobalPosition);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested
            FROM {_schema.Table("alberto_dead_letter_events")}
            WHERE processor_id = @processorId
            ORDER BY failed_at DESC
            LIMIT @limit
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("limit", limit);

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            entries.Add(new DeadLetterEntry(
                Id: reader.GetGuid(0),
                ProcessorId: reader.GetString(1),
                EventId: reader.GetGuid(2),
                EventType: reader.GetString(3),
                EventData: reader.GetString(4),
                ErrorMessage: reader.GetString(5),
                StackTrace: reader.IsDBNull(6) ? null : reader.GetString(6),
                AttemptCount: reader.GetInt32(7),
                FailedAt: reader.GetDateTime(8),
                GlobalPosition: reader.GetInt64(9),
                RetryRequested: reader.GetBoolean(10)));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string processorId, CancellationToken ct = default)
    {
        var sql = $"SELECT COUNT(*) FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var sql = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")} WHERE id = @id";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string processorId, CancellationToken ct = default)
    {
        var sql = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkForRetryAsync(string processorId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE {_schema.Table("alberto_dead_letter_events")}
            SET retry_requested = TRUE
            WHERE processor_id = @processorId
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterEntry>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken ct = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var sql = await BuildClaimSqlAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("batchSize", batchSize);
        cmd.Parameters.AddWithValue("leaseSeconds", leaseDuration.TotalSeconds);
        cmd.Parameters.AddWithValue("claimedBy", claimedBy);

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            // Parse tags from array
            IReadOnlyCollection<string>? tags = null;
            if (!reader.IsDBNull(15))
            {
                var tagsArray = reader.GetFieldValue<string[]>(15);
                tags = tagsArray ?? [];
            }

            // Parse metadata from JSONB
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!reader.IsDBNull(16))
            {
                var metadataJson = reader.GetString(16);
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
                    metadata = parsed ?? new Dictionary<string, string>();
                }
                catch
                {
                    metadata = new Dictionary<string, string>();
                }
            }

            entries.Add(new DeadLetterEntry(
                Id: reader.GetGuid(0),
                ProcessorId: reader.GetString(1),
                EventId: reader.GetGuid(2),
                EventType: reader.GetString(3),
                EventData: reader.GetString(4),
                ErrorMessage: reader.GetString(5),
                StackTrace: reader.IsDBNull(6) ? null : reader.GetString(6),
                AttemptCount: reader.GetInt32(7),
                FailedAt: reader.GetDateTime(8),
                GlobalPosition: reader.GetInt64(9),
                RetryRequested: reader.GetBoolean(10),
                TenantId: reader.IsDBNull(14) ? null : reader.GetString(14),
                Tags: tags ?? Array.Empty<string>(),
                Metadata: metadata ?? new Dictionary<string, string>(),
                CreatedAt: reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                ClaimedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                ClaimExpiresAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                ClaimedBy: reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task AbandonRetryAsync(Guid id, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE {_schema.Table("alberto_dead_letter_events")}
            SET retry_requested = FALSE,
                claimed_at = NULL,
                claim_expires_at = NULL,
                claimed_by = NULL
            WHERE id = @id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Atomically:
    //  1. picks up to @batchSize rows for the processor that are flagged for
    //     retry AND not currently claimed (or the existing claim has expired),
    //     using FOR UPDATE SKIP LOCKED so concurrent workers don't fight,
    //  2. stamps them with claimed_at / claim_expires_at / claimed_by,
    //  3. returns the claimed rows joined with the original event for tags/metadata.
    private async Task<string> BuildClaimSqlAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var hasTenantId = await EventsTableHasTenantIdAsync(conn, ct);
        var tenantSelect = hasTenantId ? "e.tenant_id" : "NULL::text AS tenant_id";

        return $"""
            WITH candidates AS (
                SELECT id
                FROM {_schema.Table("alberto_dead_letter_events")}
                WHERE retry_requested = TRUE
                  AND processor_id = @processorId
                  AND (claim_expires_at IS NULL OR claim_expires_at < now())
                ORDER BY failed_at ASC
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE {_schema.Table("alberto_dead_letter_events")} dl
                SET claimed_at       = now(),
                    claim_expires_at = now() + (@leaseSeconds || ' seconds')::interval,
                    claimed_by       = @claimedBy
                FROM candidates c
                WHERE dl.id = c.id
                RETURNING dl.id, dl.processor_id, dl.event_id, dl.event_type, dl.event_data,
                          dl.error_message, dl.stack_trace, dl.attempt_count, dl.failed_at,
                          dl.global_position, dl.retry_requested,
                          dl.claimed_at, dl.claim_expires_at, dl.claimed_by
            )
            SELECT
                cl.id, cl.processor_id, cl.event_id, cl.event_type, cl.event_data,
                cl.error_message, cl.stack_trace, cl.attempt_count, cl.failed_at,
                cl.global_position, cl.retry_requested,
                cl.claimed_at, cl.claim_expires_at, cl.claimed_by,
                {tenantSelect}, e.event_tags, e.event_metadata, e.created_at
            FROM claimed cl
            LEFT JOIN {_schema.Table("alberto_events")} e ON cl.event_id = e.event_id
            ORDER BY cl.failed_at ASC
            """;
    }

    private async Task<bool> EventsTableHasTenantIdAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = @schemaName
                  AND table_name = 'alberto_events'
                  AND column_name = 'tenant_id')
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("schemaName", SchemaName);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }
}
