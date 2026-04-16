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

    /// <inheritdoc />
    public async Task StoreAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO {_schema.Table("dead_letter_events")} (id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested)
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
            FROM {_schema.Table("dead_letter_events")}
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
        var sql = $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")} WHERE processor_id = @processorId";

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
        var sql = $"DELETE FROM {_schema.Table("dead_letter_events")} WHERE id = @id";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string processorId, CancellationToken ct = default)
    {
        var sql = $"DELETE FROM {_schema.Table("dead_letter_events")} WHERE processor_id = @processorId";

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
            UPDATE {_schema.Table("dead_letter_events")}
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
    public async Task<IReadOnlyList<DeadLetterEntry>> GetRetryRequestedWithLockAsync(
        string processorId,
        int batchSize = 10,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT
                dl.id, dl.processor_id, dl.event_id, dl.event_type, dl.event_data,
                dl.error_message, dl.stack_trace, dl.attempt_count, dl.failed_at, dl.global_position, dl.retry_requested,
                e.tenant_id, e.event_tags, e.event_metadata, e.created_at
            FROM {_schema.Table("dead_letter_events")} dl
            LEFT JOIN {_schema.Table("events")} e ON dl.event_id = e.event_id
            WHERE dl.retry_requested = TRUE AND dl.processor_id = @processorId
            ORDER BY dl.failed_at ASC
            LIMIT @batchSize
            FOR UPDATE OF dl SKIP LOCKED
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("batchSize", batchSize);

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            // Parse tags from array
            IReadOnlyCollection<string>? tags = null;
            if (!reader.IsDBNull(12))
            {
                var tagsArray = reader.GetFieldValue<string[]>(12);
                tags = tagsArray ?? [];
            }

            // Parse metadata from JSONB
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!reader.IsDBNull(13))
            {
                var metadataJson = reader.GetString(13);
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
                TenantId: reader.IsDBNull(11) ? null : reader.GetString(11),
                Tags: tags ?? Array.Empty<string>(),
                Metadata: metadata ?? new Dictionary<string, string>(),
                CreatedAt: reader.IsDBNull(14) ? null : reader.GetDateTime(14)));
        }

        return entries;
    }
}
