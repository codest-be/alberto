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
            INSERT INTO {_schema.Table("dead_letter_events")} (id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at)
            VALUES (@id, @processorId, @eventId, @eventType, @eventData::jsonb, @errorMessage, @stackTrace, @attemptCount, @failedAt)
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

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        int limit = 100,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at
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
                FailedAt: reader.GetDateTime(8)));
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
}
