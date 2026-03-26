using System.Text.Json;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditStore"/>.
/// </summary>
internal sealed class PostgresAuditStore(NpgsqlDataSource dataSource, string? schema = null) : IAuditStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly SchemaQualifier _schema = new(schema);

    public async Task LogAsync(
        string @operator,
        string action,
        string? processorId,
        Dictionary<string, object?>? details = null,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("processor_audit_log")} (operator, action, processor_id, details)
            VALUES (@operator, @action, @processor_id, @details::jsonb)
            """,
            connection);

        cmd.Parameters.AddWithValue("operator", @operator);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.Add(new NpgsqlParameter("processor_id", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)processorId ?? DBNull.Value
        });
        cmd.Parameters.AddWithValue("details", JsonSerializer.Serialize(details ?? new Dictionary<string, object?>()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        string? processorId = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        string sql;
        NpgsqlCommand cmd;

        if (processorId is not null)
        {
            sql = $"""
                SELECT id, operator, action, processor_id, details, created_at
                FROM {_schema.Table("processor_audit_log")}
                WHERE processor_id = @processor_id
                ORDER BY created_at DESC
                LIMIT @limit
                """;
            cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("processor_id", processorId);
            cmd.Parameters.AddWithValue("limit", limit);
        }
        else
        {
            sql = $"""
                SELECT id, operator, action, processor_id, details, created_at
                FROM {_schema.Table("processor_audit_log")}
                ORDER BY created_at DESC
                LIMIT @limit
                """;
            cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("limit", limit);
        }

        await using var _ = cmd;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<AuditEntry>();

        while (await reader.ReadAsync(ct))
        {
            results.Add(new AuditEntry(
                Id: reader.GetGuid(0),
                Operator: reader.GetString(1),
                Action: reader.GetString(2),
                ProcessorId: reader.IsDBNull(3) ? null : reader.GetString(3),
                Details: JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(4)) ?? new(),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return results;
    }
}
