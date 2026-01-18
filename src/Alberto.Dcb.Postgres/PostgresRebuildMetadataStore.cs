using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IRebuildMetadataStore"/>.
/// Stores rebuild version metadata in the projection_rebuild_meta table.
/// </summary>
public sealed class PostgresRebuildMetadataStore(NpgsqlDataSource dataSource, string? schema = null)
    : IRebuildMetadataStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly SchemaQualifier _schema = new(schema);

    public async Task<int> GetActiveVersionAsync(string projectionType, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT active_version
            FROM {_schema.Table("projection_rebuild_meta")}
            WHERE projection_type = @projection_type
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int version ? version : 1;
    }

    public async Task<RebuildMetadata?> GetAsync(string projectionType, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT projection_type, active_version, rebuilding_version, rebuild_status,
                   rebuild_started_at, rebuild_target_position, rebuild_completed_at
            FROM {_schema.Table("projection_rebuild_meta")}
            WHERE projection_type = @projection_type
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new RebuildMetadata
        {
            ProjectionType = reader.GetString(0),
            ActiveVersion = reader.GetInt32(1),
            RebuildingVersion = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            RebuildStatus = reader.IsDBNull(3) ? null : ParseStatus(reader.GetString(3)),
            RebuildStartedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            RebuildTargetPosition = reader.IsDBNull(5) ? null : reader.GetInt64(5),
            RebuildCompletedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
        };
    }

    public async Task SaveAsync(RebuildMetadata metadata, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("projection_rebuild_meta")}
                (projection_type, active_version, rebuilding_version, rebuild_status,
                 rebuild_started_at, rebuild_target_position, rebuild_completed_at)
            VALUES
                (@projection_type, @active_version, @rebuilding_version, @rebuild_status,
                 @rebuild_started_at, @rebuild_target_position, @rebuild_completed_at)
            ON CONFLICT (projection_type) DO UPDATE SET
                active_version = @active_version,
                rebuilding_version = @rebuilding_version,
                rebuild_status = @rebuild_status,
                rebuild_started_at = @rebuild_started_at,
                rebuild_target_position = @rebuild_target_position,
                rebuild_completed_at = @rebuild_completed_at
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", metadata.ProjectionType);
        cmd.Parameters.AddWithValue("active_version", metadata.ActiveVersion);
        cmd.Parameters.AddWithValue("rebuilding_version",
            metadata.RebuildingVersion.HasValue ? metadata.RebuildingVersion.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("rebuild_status",
            metadata.RebuildStatus.HasValue ? metadata.RebuildStatus.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("rebuild_started_at",
            metadata.RebuildStartedAt.HasValue ? metadata.RebuildStartedAt.Value.UtcDateTime : DBNull.Value);
        cmd.Parameters.AddWithValue("rebuild_target_position",
            metadata.RebuildTargetPosition.HasValue ? metadata.RebuildTargetPosition.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("rebuild_completed_at",
            metadata.RebuildCompletedAt.HasValue ? metadata.RebuildCompletedAt.Value.UtcDateTime : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string projectionType, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {_schema.Table("projection_rebuild_meta")}
            WHERE projection_type = @projection_type
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Deletes all projection state data for a specific version.
    /// Used during cleanup after version swap or rollback.
    /// </summary>
    public async Task DeleteVersionDataAsync(string projectionType, int version, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {_schema.Table("projection_states")}
            WHERE projection_type = @projection_type
              AND rebuild_version = @rebuild_version
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gets the count of projection state rows for a specific version.
    /// Useful for monitoring rebuild progress.
    /// </summary>
    public async Task<long> GetVersionRowCountAsync(string projectionType, int version, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT COUNT(*)
            FROM {_schema.Table("projection_states")}
            WHERE projection_type = @projection_type
              AND rebuild_version = @rebuild_version
            """,
            connection);

        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("rebuild_version", version);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long count ? count : 0;
    }

    private static RebuildOperationStatus? ParseStatus(string status)
    {
        return Enum.TryParse<RebuildOperationStatus>(status, ignoreCase: true, out var result)
            ? result
            : null;
    }
}
