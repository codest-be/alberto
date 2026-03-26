using Npgsql;

namespace Alberto.Cli.Data;

public record ProcessorInfo(string ProcessorId, long LastPosition, DateTime? UpdatedAt);

public record CheckpointInfo(string ProcessorId, long LastPosition, DateTime? UpdatedAt);

public record DeadLetterInfo(
    long Id,
    string ProcessorId,
    string? EventType,
    long? EventPosition,
    string? ErrorMessage,
    DateTime? OccurredAt
);

public record EventInfo(
    long GlobalPosition,
    string EventType,
    string? Tags,
    DateTime? OccurredAt
);

public record AuditLogEntry(
    long Id,
    string Action,
    string? ProcessorId,
    string? Operator,
    DateTime? OccurredAt,
    string? Details
);

public record SystemInfo(
    long? GlobalPosition,
    long ProcessorCount,
    long DeadLetterCount,
    DateTime? LastEventAt
);

public record ProjectionState(
    string DocumentId,
    string TenantId,
    DateTime? UpdatedAt
);

public record TenantLease(
    string TenantId,
    string ConsumerId,
    string? ReplicaId,
    DateTime? ExpiresAt
);

public class CliDataAccess
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public CliDataAccess(NpgsqlDataSource dataSource, string schema)
    {
        _dataSource = dataSource;
        _schema = schema;
    }

    public async Task<long?> GetGlobalPositionAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX(global_position) FROM {_schema}.events";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    public async Task<List<ProcessorInfo>> GetProcessorsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema}.processor_checkpoints
            ORDER BY processor_id
            """;

        var result = new List<ProcessorInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ProcessorInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)
            ));
        }

        return result;
    }

    public async Task<List<CheckpointInfo>> GetCheckpointsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema}.processor_checkpoints
            ORDER BY processor_id
            """;

        var result = new List<CheckpointInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CheckpointInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)
            ));
        }

        return result;
    }

    public async Task<CheckpointInfo?> GetSingleCheckpointAsync(string processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema}.processor_checkpoints
            WHERE processor_id = @processorId
            """;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new CheckpointInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)
            );
        }

        return null;
    }

    public async Task<List<DeadLetterInfo>> GetDeadLettersAsync(
        string? processorId,
        string? type,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(processorId))
        {
            where.Add("processor_id = @processorId");
            cmd.Parameters.AddWithValue("processorId", processorId);
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            where.Add("event_type = @type");
            cmd.Parameters.AddWithValue("type", type);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        cmd.Parameters.AddWithValue("limit", limit);

        cmd.CommandText = $"""
            SELECT id, processor_id, event_type, event_position, error_message, occurred_at
            FROM {_schema}.processor_dead_letters
            {whereClause}
            ORDER BY id DESC
            LIMIT @limit
            """;

        var result = new List<DeadLetterInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DeadLetterInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            ));
        }

        return result;
    }

    public async Task<List<EventInfo>> GetEventsAsync(
        string? type,
        string? tag,
        long afterPosition,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "global_position > @afterPosition" };
        cmd.Parameters.AddWithValue("afterPosition", afterPosition);

        if (!string.IsNullOrWhiteSpace(type))
        {
            where.Add("event_type = @type");
            cmd.Parameters.AddWithValue("type", type);
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            where.Add("tags @> ARRAY[@tag]");
            cmd.Parameters.AddWithValue("tag", tag);
        }

        cmd.Parameters.AddWithValue("limit", limit);

        cmd.CommandText = $"""
            SELECT global_position, event_type, array_to_string(tags, ','), occurred_at
            FROM {_schema}.events
            WHERE {string.Join(" AND ", where)}
            ORDER BY global_position
            LIMIT @limit
            """;

        var result = new List<EventInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new EventInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)
            ));
        }

        return result;
    }

    public async Task ResetCheckpointAsync(string processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_schema}.processor_checkpoints WHERE processor_id = @processorId";
        cmd.Parameters.AddWithValue("processorId", processorId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetCheckpointAsync(string processorId, long position, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_schema}.processor_checkpoints (processor_id, last_position, updated_at)
            VALUES (@processorId, @position, NOW())
            ON CONFLICT (processor_id) DO UPDATE
              SET last_position = @position, updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("position", position);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DismissDeadLettersAsync(string? processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(processorId))
        {
            cmd.CommandText = $"DELETE FROM {_schema}.processor_dead_letters WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
        }
        else
        {
            cmd.CommandText = $"DELETE FROM {_schema}.processor_dead_letters";
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountDeadLettersAsync(string? processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(processorId))
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema}.processor_dead_letters WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
        }
        else
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema}.processor_dead_letters";
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<bool> AuditLogExistsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_tables
                WHERE schemaname = @schema AND tablename = 'processor_audit_log'
            )
            """;
        cmd.Parameters.AddWithValue("schema", _schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    public async Task<List<AuditLogEntry>> GetAuditLogAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.CommandText = $"""
            SELECT id, action, processor_id, operator, occurred_at, details
            FROM {_schema}.processor_audit_log
            ORDER BY id DESC
            LIMIT @limit
            """;

        var result = new List<AuditLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AuditLogEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)
            ));
        }

        return result;
    }

    public async Task WriteAuditLogAsync(
        string action,
        string processorId,
        string operatorName,
        string? details,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_schema}.processor_audit_log (action, processor_id, operator, occurred_at, details)
            VALUES (@action, @processorId, @operator, NOW(), @details)
            """;
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("operator", operatorName);
        cmd.Parameters.AddWithValue("details", (object?)details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        long? globalPosition = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT MAX(global_position) FROM {_schema}.events";
            var result = await cmd.ExecuteScalarAsync(ct);
            globalPosition = result is DBNull or null ? null : Convert.ToInt64(result);
        }

        long processorCount = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema}.processor_checkpoints";
            var result = await cmd.ExecuteScalarAsync(ct);
            processorCount = Convert.ToInt64(result);
        }

        long deadLetterCount = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema}.processor_dead_letters";
            var result = await cmd.ExecuteScalarAsync(ct);
            deadLetterCount = Convert.ToInt64(result);
        }

        DateTime? lastEventAt = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT created_at FROM {_schema}.events ORDER BY global_position DESC LIMIT 1";
            var result = await cmd.ExecuteScalarAsync(ct);
            lastEventAt = result is DBNull or null ? null : Convert.ToDateTime(result);
        }

        return new SystemInfo(globalPosition, processorCount, deadLetterCount, lastEventAt);
    }

    public async Task<List<string>> GetProjectionTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT projection_type FROM {_schema}.projection_states ORDER BY 1
            """;

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<List<ProjectionState>> GetProjectionStatesAsync(
        string type,
        string? tenant,
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("tenant", (object?)tenant ?? DBNull.Value);
        cmd.Parameters.AddWithValue("search", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", limit);

        cmd.CommandText = $"""
            SELECT document_id, tenant_id, updated_at
            FROM {_schema}.projection_states
            WHERE projection_type = @type
            AND (@tenant IS NULL OR tenant_id = @tenant)
            AND (@search IS NULL OR document_id ILIKE '%' || @search || '%')
            ORDER BY updated_at DESC
            LIMIT @limit
            """;

        var result = new List<ProjectionState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ProjectionState(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)
            ));
        }

        return result;
    }

    public async Task<bool> TenantLeasesTableExistsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_tables
                WHERE schemaname = @schema AND tablename = 'tenant_leases'
            )
            """;
        cmd.Parameters.AddWithValue("schema", _schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    public async Task<List<TenantLease>> GetTenantLeasesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT tenant_id, consumer_id, replica_id, expires_at
            FROM {_schema}.tenant_leases
            ORDER BY tenant_id
            """;

        var result = new List<TenantLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TenantLease(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)
            ));
        }

        return result;
    }

    public async Task<int> ReleaseTenantLeasesAsync(string? processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.Parameters.AddWithValue("processorId", (object?)processorId ?? DBNull.Value);
        cmd.CommandText = $"""
            DELETE FROM {_schema}.tenant_leases
            WHERE (@processorId IS NULL OR consumer_id = @processorId)
            """;

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(long RewindPosition, int DeletedCount)> RetryByRewindAsync(
        string processorId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        long earliestPosition;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT MIN(global_position) FROM {_schema}.processor_dead_letters WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            var result = await cmd.ExecuteScalarAsync(ct);
            earliestPosition = result is DBNull or null ? 0 : Convert.ToInt64(result);
        }

        var rewindPosition = earliestPosition - 1;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                UPDATE {_schema}.processor_checkpoints
                SET last_position = @position, updated_at = now()
                WHERE processor_id = @processorId
                """;
            cmd.Parameters.AddWithValue("processorId", processorId);
            cmd.Parameters.AddWithValue("position", rewindPosition);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM {_schema}.processor_dead_letters WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        return (rewindPosition, deletedCount);
    }
}
