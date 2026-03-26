using System.Text.Json;
using Alberto.Dcb.Messaging;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IOutboxStore"/>.
/// </summary>
public sealed class PostgresOutboxStore(NpgsqlDataSource dataSource, string? schema = null) : IOutboxStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly SchemaQualifier _schema = new(schema);

    /// <inheritdoc/>
    public async Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("outbox_entries")}
                (id, source_event_id, message_type, version, payload, metadata, status, retry_count, last_error, created_at, delivered_at)
            VALUES
                (@id, @source_event_id, @message_type, @version, @payload::jsonb, @metadata::jsonb, @status, @retry_count, @last_error, @created_at, @delivered_at)
            ON CONFLICT (source_event_id) DO NOTHING
            """,
            connection);

        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("source_event_id", entry.SourceEventId);
        cmd.Parameters.AddWithValue("message_type", entry.MessageType);
        cmd.Parameters.AddWithValue("version", entry.Version);
        cmd.Parameters.AddWithValue("payload", entry.Payload);
        cmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(entry.Metadata));
        cmd.Parameters.AddWithValue("status", entry.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("retry_count", entry.RetryCount);
        cmd.Parameters.Add(new NpgsqlParameter("last_error", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)entry.LastError ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("created_at", entry.CreatedAt);
        cmd.Parameters.Add(new NpgsqlParameter("delivered_at", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = (object?)entry.DeliveredAt ?? DBNull.Value
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT id, source_event_id, message_type, version, payload, metadata,
                   status, retry_count, last_error, created_at, delivered_at
            FROM {_schema.Table("outbox_entries")}
            WHERE status = 'pending'
            ORDER BY created_at
            LIMIT @limit
            """,
            connection);

        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<OutboxEntry>();

        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task MarkDeliveredAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_schema.Table("outbox_entries")}
            SET status = 'delivered', delivered_at = now()
            WHERE id = @id
            """,
            connection);

        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_schema.Table("outbox_entries")}
            SET status = 'failed', retry_count = retry_count + 1, last_error = @error
            WHERE id = @id
            """,
            connection);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("error", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task RetryFailedAsync(string? messageType = null, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        string sql;
        NpgsqlCommand cmd;

        if (messageType is not null)
        {
            sql = $"""
                UPDATE {_schema.Table("outbox_entries")}
                SET status = 'pending', retry_count = 0, last_error = NULL
                WHERE status = 'failed' AND message_type = @message_type
                """;
            cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("message_type", messageType);
        }
        else
        {
            sql = $"""
                UPDATE {_schema.Table("outbox_entries")}
                SET status = 'pending', retry_count = 0, last_error = NULL
                WHERE status = 'failed'
                """;
            cmd = new NpgsqlCommand(sql, connection);
        }

        await using var _ = cmd;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task PurgeDeliveredAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {_schema.Table("outbox_entries")}
            WHERE status = 'delivered' AND delivered_at < @before
            """,
            connection);

        cmd.Parameters.AddWithValue("before", before);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static OutboxEntry ReadEntry(NpgsqlDataReader reader)
    {
        var statusString = reader.GetString(6);
        var status = statusString switch
        {
            "pending" => OutboxEntryStatus.Pending,
            "delivered" => OutboxEntryStatus.Delivered,
            "failed" => OutboxEntryStatus.Failed,
            _ => OutboxEntryStatus.Pending
        };

        var metadataJson = reader.GetString(5);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson) ?? new();

        return new OutboxEntry(
            Id: reader.GetGuid(0),
            SourceEventId: reader.GetGuid(1),
            MessageType: reader.GetString(2),
            Version: reader.GetString(3),
            Payload: reader.GetString(4),
            Metadata: metadata,
            Status: status,
            RetryCount: reader.GetInt32(7),
            LastError: reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
            DeliveredAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
    }
}
