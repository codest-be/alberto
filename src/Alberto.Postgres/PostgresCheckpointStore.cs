using Alberto.Checkpoints;
using Alberto.Subscriptions;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ICheckpointStore"/>.
/// Uses the alberto_processor_checkpoints table.
/// </summary>
public sealed class PostgresCheckpointStore : IFencedCheckpointStore, ICheckpointInventory, IProcessorFaultStore
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

    public async Task<long?> GetAsync(string processorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT last_position FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processor_id",
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : null;
    }

    public async Task SaveAsync(string processorId, long position, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
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

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetAsync(string processorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processor_id",
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListProcessorIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT processor_id FROM {_schema.Table("alberto_processor_checkpoints")}",
            connection);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));

        return ids;
    }

    /// <summary>
    /// Sets the checkpoint for <paramref name="processorId"/> to exactly <paramref name="position"/>,
    /// bypassing the <c>GREATEST</c> guard that normally prevents moving a checkpoint backward.
    /// Use for operator-initiated rewinds.
    /// </summary>
    public async Task RewindAsync(string processorId, long position, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("alberto_processor_checkpoints")} (processor_id, last_position, updated_at)
            VALUES (@processor_id, @last_position, now())
            ON CONFLICT (processor_id) DO UPDATE
            SET last_position = @last_position,
                updated_at = now(),
                faulted_at = NULL,
                fault_message = NULL,
                fault_stack_trace = NULL,
                fault_position = NULL,
                fault_event_type = NULL,
                fault_tenant_id = NULL
            """,
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("last_position", position);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <remarks>
    /// Inserts last_position 0 when no checkpoint row exists yet. <see cref="GetAsync"/> then
    /// returns 0 rather than null, which does not change where the processor resumes, because
    /// the control loop already reads a missing checkpoint as 0. The row does make the
    /// processor appear in <see cref="ListProcessorIdsAsync"/>. Only the fault columns are
    /// touched on conflict.
    /// </remarks>
    async Task IProcessorFaultStore.RecordFaultAsync(
        string processorId, ProcessorFaultRecord fault, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema.Table("alberto_processor_checkpoints")}
                (processor_id, last_position, faulted_at, fault_message, fault_stack_trace,
                 fault_position, fault_event_type, fault_tenant_id)
            VALUES (@processor_id, 0, @faulted_at, @fault_message, @fault_stack_trace,
                    @fault_position, @fault_event_type, @fault_tenant_id)
            ON CONFLICT (processor_id) DO UPDATE
            SET faulted_at = @faulted_at,
                fault_message = @fault_message,
                fault_stack_trace = @fault_stack_trace,
                fault_position = @fault_position,
                fault_event_type = @fault_event_type,
                fault_tenant_id = @fault_tenant_id
            """,
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("faulted_at", fault.FaultedAt);
        cmd.Parameters.AddWithValue("fault_message", fault.Message);
        cmd.Parameters.AddWithValue("fault_stack_trace", (object?)fault.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fault_position", (object?)fault.Position ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fault_event_type", (object?)fault.EventType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fault_tenant_id", (object?)fault.TenantId ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task IProcessorFaultStore.ClearFaultAsync(string processorId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_schema.Table("alberto_processor_checkpoints")}
            SET faulted_at = NULL,
                fault_message = NULL,
                fault_stack_trace = NULL,
                fault_position = NULL,
                fault_event_type = NULL,
                fault_tenant_id = NULL
            WHERE processor_id = @processor_id
            """,
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<ProcessorFaultRecord?> IProcessorFaultStore.GetFaultAsync(
        string processorId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT faulted_at, fault_message, fault_stack_trace,
                   fault_position, fault_event_type, fault_tenant_id
            FROM {_schema.Table("alberto_processor_checkpoints")}
            WHERE processor_id = @processor_id AND faulted_at IS NOT NULL
            """,
            connection);

        cmd.Parameters.AddWithValue("processor_id", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ProcessorFaultRecord(
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task<bool> SaveIfLeaseHeldAsync(
        string processorId, long position, string consumerId, string replicaId,
        long fenceToken, bool useProcessorLeaseFencing = false, CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand();

        // Tenant leases carry no generation — a replica holds one per tenant and none of them
        // names an owner of this processor — so that variant keeps the four-argument signature
        // and the token is not sent. See migration 021.
        cmd.CommandText = useProcessorLeaseFencing
            ? $"SELECT {_schema.Function("alberto_save_checkpoint_if_processor_lease_held")}" +
              "(@processorId, @consumerId, @replicaId, @position, @fenceToken)"
            : $"SELECT {_schema.Function("alberto_save_checkpoint_if_lease_held")}" +
              "(@processorId, @consumerId, @replicaId, @position)";

        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("consumerId", consumerId);
        cmd.Parameters.AddWithValue("replicaId", replicaId);
        cmd.Parameters.AddWithValue("position", position);

        if (useProcessorLeaseFencing)
            cmd.Parameters.AddWithValue("fenceToken", fenceToken);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
