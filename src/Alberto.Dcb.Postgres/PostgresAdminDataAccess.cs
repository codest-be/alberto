using System.Text.Json;
using Alberto.Dcb;
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
        string? searchTerm,
        DateTimeOffset? updatedAfter,
        DateTimeOffset? updatedBefore,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Build WHERE clause dynamically
        var conditions = new List<string> { "projection_type = @projection_type" };
        var parameters = new Dictionary<string, object> { ["projection_type"] = projectionType };

        if (tenantId is not null)
        {
            conditions.Add("tenant_id = @tenant_id");
            parameters["tenant_id"] = tenantId;
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("document_id ILIKE @search_term");
            parameters["search_term"] = $"%{searchTerm}%";
        }

        if (updatedAfter is not null)
        {
            conditions.Add("updated_at >= @updated_after");
            parameters["updated_after"] = updatedAfter.Value.UtcDateTime;
        }

        if (updatedBefore is not null)
        {
            conditions.Add("updated_at <= @updated_before");
            parameters["updated_before"] = updatedBefore.Value.UtcDateTime;
        }

        var whereClause = $"WHERE {string.Join(" AND ", conditions)}";

        // Count total
        var countSql = $"SELECT COUNT(*) FROM {_schema.Table("projection_states")} {whereClause}";
        await using var countCmd = new NpgsqlCommand(countSql, connection);
        foreach (var param in parameters)
            countCmd.Parameters.AddWithValue(param.Key, param.Value);

        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page
        var offset = (page - 1) * pageSize;
        var querySql = $"""
            SELECT tenant_id, projection_type, document_id, state, updated_at
            FROM {_schema.Table("projection_states")}
            {whereClause}
            ORDER BY document_id
            LIMIT @limit OFFSET @offset
            """;

        await using var queryCmd = new NpgsqlCommand(querySql, connection);
        foreach (var param in parameters)
            queryCmd.Parameters.AddWithValue(param.Key, param.Value);
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

    public async Task<IReadOnlyList<string>> GetProjectionTenantsAsync(string projectionType, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT tenant_id FROM {_schema.Table("projection_states")} WHERE projection_type = @projection_type ORDER BY tenant_id",
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);

        var tenants = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            tenants.Add(reader.GetString(0));
        }

        return tenants;
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
        string? eventType,
        string? searchTerm,
        DateTimeOffset? failedAfter,
        DateTimeOffset? failedBefore,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Build WHERE clause dynamically
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();

        if (processorId is not null)
        {
            conditions.Add("processor_id = @processor_id");
            parameters["processor_id"] = processorId;
        }

        if (eventType is not null)
        {
            conditions.Add("event_type = @event_type");
            parameters["event_type"] = eventType;
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("error_message ILIKE @search_term");
            parameters["search_term"] = $"%{searchTerm}%";
        }

        if (failedAfter is not null)
        {
            conditions.Add("failed_at >= @failed_after");
            parameters["failed_after"] = failedAfter.Value.UtcDateTime;
        }

        if (failedBefore is not null)
        {
            conditions.Add("failed_at <= @failed_before");
            parameters["failed_before"] = failedBefore.Value.UtcDateTime;
        }

        var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";

        // Count total
        var countSql = $"SELECT COUNT(*) FROM {_schema.Table("dead_letter_events")} {whereClause}";
        await using var countCmd = new NpgsqlCommand(countSql, connection);
        foreach (var param in parameters)
            countCmd.Parameters.AddWithValue(param.Key, param.Value);

        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page
        var offset = (page - 1) * pageSize;
        var querySql = $"""
            SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at
            FROM {_schema.Table("dead_letter_events")}
            {whereClause}
            ORDER BY failed_at DESC
            LIMIT @limit OFFSET @offset
            """;

        await using var queryCmd = new NpgsqlCommand(querySql, connection);
        foreach (var param in parameters)
            queryCmd.Parameters.AddWithValue(param.Key, param.Value);
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

    public async Task<IReadOnlyList<string>> GetDeadLetterEventTypesAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT event_type FROM {_schema.Table("dead_letter_events")} ORDER BY event_type",
            connection);

        var types = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            types.Add(reader.GetString(0));
        }

        return types;
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

    public async Task<IEventEnvelope?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT global_position, tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at
            FROM {_schema.Table("events")}
            WHERE event_id = @event_id
            """,
            connection);

        cmd.Parameters.AddWithValue("event_id", eventId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var globalPosition = reader.GetInt64(0);
            var tenantId = reader.GetString(1);
            var id = reader.GetGuid(2);
            var eventType = reader.GetString(3);
            var eventTags = reader.GetFieldValue<string[]>(4);
            var eventData = reader.GetString(5);
            var eventMetadata = reader.GetString(6);
            var createdAt = reader.GetDateTime(7);

            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(eventMetadata) ?? [];

            return new EventEnvelope
            {
                Id = id,
                TenantId = tenantId,
                GlobalPosition = globalPosition,
                EventType = new EventType(eventType),
                Tags = eventTags.Select(EventTag.Parse).ToArray(),
                EventData = eventData,
                Metadata = metadata,
                CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
            };
        }

        return null;
    }

    public async Task ClearProjectionStatesAsync(string projectionType, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {_schema.Table("projection_states")} WHERE projection_type = @projection_type",
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
