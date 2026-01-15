using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Admin.Internal;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IAdminDataAccess"/>.
/// </summary>
public sealed class PostgresAdminDataAccess : IAdminDataAccess
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    public PostgresAdminDataAccess(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
    }

    public async Task<IReadOnlyList<CheckpointDto>> ListCheckpointsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT processor_id, last_position, updated_at FROM {_schema.Table("processor_checkpoints")} ORDER BY processor_id",
            connection);

        var checkpoints = new List<CheckpointDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            checkpoints.Add(new CheckpointDto(
                ProcessorId: reader.GetString(0),
                LastPosition: reader.GetInt64(1),
                UpdatedAt: reader.GetDateTime(2)));
        }

        return checkpoints;
    }

    public async Task<IReadOnlyList<string>> ListProjectionTypesAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT projection_type FROM {_schema.Table("projection_states")} ORDER BY projection_type",
            connection);

        var types = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }

    public async Task<PagedResult<ProjectionStateDto>> ListProjectionStatesAsync(
        string projectionType,
        string? tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Count total
        var countSql = tenantId is null
            ? $"SELECT COUNT(*) FROM {_schema.Table("projection_states")} WHERE projection_type = @projection_type"
            : $"SELECT COUNT(*) FROM {_schema.Table("projection_states")} WHERE projection_type = @projection_type AND tenant_id = @tenant_id";

        await using var countCmd = new NpgsqlCommand(countSql, connection);
        countCmd.Parameters.AddWithValue("projection_type", projectionType);
        if (tenantId is not null)
            countCmd.Parameters.AddWithValue("tenant_id", tenantId);

        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page
        var offset = (page - 1) * pageSize;
        var querySql = tenantId is null
            ? $"""
              SELECT tenant_id, projection_type, document_id, state, updated_at
              FROM {_schema.Table("projection_states")}
              WHERE projection_type = @projection_type
              ORDER BY document_id
              LIMIT @limit OFFSET @offset
              """
            : $"""
              SELECT tenant_id, projection_type, document_id, state, updated_at
              FROM {_schema.Table("projection_states")}
              WHERE projection_type = @projection_type AND tenant_id = @tenant_id
              ORDER BY document_id
              LIMIT @limit OFFSET @offset
              """;

        await using var queryCmd = new NpgsqlCommand(querySql, connection);
        queryCmd.Parameters.AddWithValue("projection_type", projectionType);
        if (tenantId is not null)
            queryCmd.Parameters.AddWithValue("tenant_id", tenantId);
        queryCmd.Parameters.AddWithValue("limit", pageSize);
        queryCmd.Parameters.AddWithValue("offset", offset);

        var items = new List<ProjectionStateDto>();
        await using var reader = await queryCmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new ProjectionStateDto(
                TenantId: reader.GetString(0),
                ProjectionType: reader.GetString(1),
                DocumentId: reader.GetString(2),
                State: reader.GetString(3),
                UpdatedAt: reader.GetDateTime(4)));
        }

        return new PagedResult<ProjectionStateDto>(
            Items: items,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            TotalPages: (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<ProjectionStateDto?> GetProjectionStateAsync(
        string projectionType,
        string documentId,
        string? tenantId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var sql = tenantId is null
            ? $"""
              SELECT tenant_id, projection_type, document_id, state, updated_at
              FROM {_schema.Table("projection_states")}
              WHERE projection_type = @projection_type AND document_id = @document_id
              LIMIT 1
              """
            : $"""
              SELECT tenant_id, projection_type, document_id, state, updated_at
              FROM {_schema.Table("projection_states")}
              WHERE projection_type = @projection_type AND document_id = @document_id AND tenant_id = @tenant_id
              """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("document_id", documentId);
        if (tenantId is not null)
            cmd.Parameters.AddWithValue("tenant_id", tenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new ProjectionStateDto(
                TenantId: reader.GetString(0),
                ProjectionType: reader.GetString(1),
                DocumentId: reader.GetString(2),
                State: reader.GetString(3),
                UpdatedAt: reader.GetDateTime(4));
        }

        return null;
    }

    public async Task<PagedResult<DeadLetterDto>> ListDeadLettersAsync(
        string? processorId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Count total
        var countSql = processorId is null
            ? $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")}"
            : $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")} WHERE processor_id = @processor_id";

        await using var countCmd = new NpgsqlCommand(countSql, connection);
        if (processorId is not null)
            countCmd.Parameters.AddWithValue("processor_id", processorId);

        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page
        var offset = (page - 1) * pageSize;
        var querySql = processorId is null
            ? $"""
              SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at
              FROM {_schema.Table("dead_letter_events")}
              ORDER BY failed_at DESC
              LIMIT @limit OFFSET @offset
              """
            : $"""
              SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at
              FROM {_schema.Table("dead_letter_events")}
              WHERE processor_id = @processor_id
              ORDER BY failed_at DESC
              LIMIT @limit OFFSET @offset
              """;

        await using var queryCmd = new NpgsqlCommand(querySql, connection);
        if (processorId is not null)
            queryCmd.Parameters.AddWithValue("processor_id", processorId);
        queryCmd.Parameters.AddWithValue("limit", pageSize);
        queryCmd.Parameters.AddWithValue("offset", offset);

        var items = new List<DeadLetterDto>();
        await using var reader = await queryCmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            items.Add(new DeadLetterDto(
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

        return new PagedResult<DeadLetterDto>(
            Items: items,
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            TotalPages: (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<DeadLetterDto?> GetDeadLetterAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at
            FROM {_schema.Table("dead_letter_events")}
            WHERE id = @id
            """,
            connection);

        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            return new DeadLetterDto(
                Id: reader.GetGuid(0),
                ProcessorId: reader.GetString(1),
                EventId: reader.GetGuid(2),
                EventType: reader.GetString(3),
                EventData: reader.GetString(4),
                ErrorMessage: reader.GetString(5),
                StackTrace: reader.IsDBNull(6) ? null : reader.GetString(6),
                AttemptCount: reader.GetInt32(7),
                FailedAt: reader.GetDateTime(8));
        }

        return null;
    }

    public async Task<int> GetDeadLetterCountAsync(string? processorId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var sql = processorId is null
            ? $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")}"
            : $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")} WHERE processor_id = @processor_id";

        await using var cmd = new NpgsqlCommand(sql, connection);
        if (processorId is not null)
            cmd.Parameters.AddWithValue("processor_id", processorId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
