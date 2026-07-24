using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ICheckpointStore"/>.
/// Uses the alberto_processor_checkpoints table.
/// </summary>
public sealed class PostgresCheckpointStore : IFencedCheckpointStore, ICheckpointInventory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    /// <summary>
    /// Creates a new PostgresCheckpointStore.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name. Can be null for default schema.</param>
    public PostgresCheckpointStore(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
    }

    public async Task<long?> GetAsync(string processorId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT last_position FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processor_id",
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
            INSERT INTO {_schema.Table("alberto_processor_checkpoints")} (processor_id, last_position, updated_at)
            VALUES (@processor_id, @last_position, now())
            ON CONFLICT (processor_id) DO UPDATE
            SET last_position = GREATEST({_schema.Table("alberto_processor_checkpoints")}.last_position, @last_position),
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
            $"DELETE FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processor_id",
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            $"SELECT processor_id FROM {_schema.Table("alberto_processor_checkpoints")}",
            connection);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));

        return ids;
    }

    public async Task<bool> SaveIfLeaseHeldAsync(
        string processorId, long position, string consumerId, string replicaId,
        bool useProcessorLeaseFencing = false, CancellationToken ct = default)
    {
        var functionName = useProcessorLeaseFencing
            ? "alberto_save_checkpoint_if_processor_lease_held"
            : "alberto_save_checkpoint_if_lease_held";

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"SELECT {_schema.Function(functionName)}(@processorId, @consumerId, @replicaId, @position)";
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("consumerId", consumerId);
        cmd.Parameters.AddWithValue("replicaId", replicaId);
        cmd.Parameters.AddWithValue("position", position);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }
}
