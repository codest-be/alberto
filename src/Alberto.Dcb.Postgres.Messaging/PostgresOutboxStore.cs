using System.Text.Json;
using Alberto.Dcb.Messaging;
using Alberto.Dcb.Postgres;
using Npgsql;

namespace Alberto.Dcb.Postgres.Messaging;

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
            INSERT INTO {_schema.Table("alberto_outbox_entries")}
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
    public async Task<IReadOnlyList<OutboxClaim>> ClaimPendingAsync(
        int limit,
        TimeSpan claimLease,
        string claimedBy,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (claimLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimLease), "The outbox claim lease must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var claimToken = Guid.NewGuid();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT id
                FROM {_schema.Table("alberto_outbox_entries")}
                WHERE status = 'pending'
                   OR (
                       status = 'processing'
                       AND (claim_expires_at IS NULL OR claim_expires_at <= now())
                   )
                ORDER BY created_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE {_schema.Table("alberto_outbox_entries")} e
                SET status = 'processing',
                    claim_id = @claim_id,
                    claimed_by = @claimed_by,
                    claim_expires_at = now() + @claim_lease
                FROM candidates
                WHERE e.id = candidates.id
                RETURNING e.id, e.source_event_id, e.message_type, e.version, e.payload,
                          e.metadata, e.status, e.retry_count, e.last_error,
                          e.created_at, e.delivered_at, e.claim_id, e.claim_expires_at
            )
            SELECT id, source_event_id, message_type, version, payload, metadata,
                   status, retry_count, last_error, created_at, delivered_at,
                   claim_id, claim_expires_at
            FROM claimed
            ORDER BY created_at
            """,
            connection);

        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("claim_id", claimToken);
        cmd.Parameters.AddWithValue("claimed_by", claimedBy);
        cmd.Parameters.AddWithValue("claim_lease", claimLease);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<OutboxClaim>();

        while (await reader.ReadAsync(ct))
        {
            var entry = ReadEntry(reader);
            results.Add(new OutboxClaim(
                entry,
                reader.GetGuid(11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkDeliveredAsync(OutboxClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_schema.Table("alberto_outbox_entries")}
            SET status = 'delivered',
                delivered_at = now(),
                claim_id = NULL,
                claimed_by = NULL,
                claim_expires_at = NULL
            WHERE id = @id
              AND status = 'processing'
              AND claim_id = @claim_id
              AND claim_expires_at > now()
            """,
            connection);

        cmd.Parameters.AddWithValue("id", claim.Entry.Id);
        cmd.Parameters.AddWithValue("claim_id", claim.Token);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> MarkFailedAsync(
        OutboxClaim claim,
        string error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(error);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_schema.Table("alberto_outbox_entries")}
            SET status = 'failed',
                retry_count = retry_count + 1,
                last_error = @error,
                claim_id = NULL,
                claimed_by = NULL,
                claim_expires_at = NULL
            WHERE id = @id
              AND status = 'processing'
              AND claim_id = @claim_id
              AND claim_expires_at > now()
            """,
            connection);

        cmd.Parameters.AddWithValue("id", claim.Entry.Id);
        cmd.Parameters.AddWithValue("claim_id", claim.Token);
        cmd.Parameters.AddWithValue("error", error);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
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
                UPDATE {_schema.Table("alberto_outbox_entries")}
                SET status = 'pending',
                    retry_count = 0,
                    last_error = NULL,
                    claim_id = NULL,
                    claimed_by = NULL,
                    claim_expires_at = NULL
                WHERE status = 'failed' AND message_type = @message_type
                """;
            cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("message_type", messageType);
        }
        else
        {
            sql = $"""
                UPDATE {_schema.Table("alberto_outbox_entries")}
                SET status = 'pending',
                    retry_count = 0,
                    last_error = NULL,
                    claim_id = NULL,
                    claimed_by = NULL,
                    claim_expires_at = NULL
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
            DELETE FROM {_schema.Table("alberto_outbox_entries")}
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
            // 'processing' means the row has a time-bounded claim lease.
            "processing" => OutboxEntryStatus.Processing,
            "delivered" => OutboxEntryStatus.Delivered,
            "failed" => OutboxEntryStatus.Failed,
            _ => throw new InvalidOperationException($"Unknown outbox entry status: '{statusString}'")
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
