using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ICheckpointStore"/>.
/// Uses the processor_checkpoints table.
/// </summary>
public sealed class PostgresCheckpointStore : ICheckpointStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    public PostgresCheckpointStore(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
    }

    public async Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT last_position FROM {_schema.Table("processor_checkpoints")} WHERE processor_id = @processor_id",
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long position ? position : null;
    }

    public async Task SaveAsync(string processorId, long position, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("processor_checkpoints")} (processor_id, last_position, updated_at)
            VALUES (@processor_id, @last_position, now())
            ON CONFLICT (processor_id) DO UPDATE
            SET last_position = GREATEST({_schema.Table("processor_checkpoints")}.last_position, @last_position),
                updated_at = now()
            """,
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("last_position", position);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetAsync(string processorId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {_schema.Table("processor_checkpoints")} WHERE processor_id = @processor_id",
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
