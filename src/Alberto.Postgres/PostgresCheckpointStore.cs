using Alberto.Checkpoints;
using Alberto.Subscriptions;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ICheckpointStore"/>.
/// Uses the alberto_processor_checkpoints table.
/// </summary>
public sealed class PostgresCheckpointStore : IFencedCheckpointStore, ICheckpointInventory, IProcessorFaultStore, ICheckpointBatchStore
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

    async Task<IReadOnlyDictionary<string, long?>> ICheckpointBatchStore.GetManyAsync(
        IReadOnlyCollection<string> processorIds, CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT processor_id, last_position FROM {_schema.Table("alberto_processor_checkpoints")} " +
            "WHERE processor_id = ANY(@processor_ids)");
        cmd.Parameters.AddWithValue("processor_ids", processorIds.ToArray());

        var stored = processorIds.ToDictionary(id => id, _ => (long?)null, StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            stored[reader.GetString(0)] = reader.GetInt64(1);
        return stored;
    }

    /// <remarks>
    /// One statement, so the reset check, the lease check and the write share one snapshot,
    /// as in <c>alberto_save_checkpoint_if_processor_lease_held</c>. Rows that exist are
    /// UPDATEd, because the floor has to be compared with the row being overwritten and
    /// <c>ON CONFLICT</c> cannot see it; only rows with no floor (this replica never persisted
    /// one) are INSERTed, so an operator's reset is never recreated. The final SELECT reads the
    /// pre-statement snapshot, which is what tells a reset apart from a lost lease.
    /// </remarks>
    async Task<IReadOnlyDictionary<string, CheckpointWriteOutcome>> ICheckpointBatchStore.SaveManyAsync(
        IReadOnlyList<CheckpointWrite> writes, FencingContext? fencing, CancellationToken cancellationToken)
    {
        var checkpoints = _schema.Table("alberto_processor_checkpoints");
        var processorFencing = fencing is { UseProcessorLeaseFencing: true };

        // Which input rows may write. Processor leases also pin the generation; a tenant lease
        // only says the replica is alive (see alberto_save_checkpoint_if_lease_held).
        var leased = fencing switch
        {
            null => "SELECT * FROM input",
            { UseProcessorLeaseFencing: true } =>
                $"""
                SELECT i.* FROM input i
                JOIN {_schema.Table("alberto_processor_leases")} l
                  ON l.consumer_id = @consumer_id AND l.processor_id = i.processor_id
                 AND l.replica_id = @replica_id AND l.expires_at > now() AND l.fence_token = i.fence_token
                """,
            _ =>
                $"""
                SELECT * FROM input WHERE EXISTS (
                    SELECT 1 FROM {_schema.Table("alberto_tenant_leases")}
                    WHERE consumer_id = @consumer_id AND replica_id = @replica_id AND expires_at > now())
                """,
        };

        // Fence guard and token bookkeeping only for processor leases, exactly as the
        // single-row functions: the other two modes never read or write fence_token.
        var updateGuard = processorFencing ? "AND c.fence_token <= x.fence_token" : "";
        var updateToken = processorFencing ? "fence_token = GREATEST(c.fence_token, x.fence_token)," : "";
        var insertColumns = processorFencing ? ", fence_token" : "";
        var insertValues = processorFencing ? ", x.fence_token" : "";
        var conflictToken = processorFencing ? $"fence_token = EXCLUDED.fence_token," : "";
        var conflictGuard = processorFencing ? $"WHERE {checkpoints}.fence_token <= EXCLUDED.fence_token" : "";

        await using var cmd = _dataSource.CreateCommand(
            $"""
            WITH input AS (
                SELECT * FROM unnest(@processor_ids, @positions, @floors, @fence_tokens)
                    AS i(processor_id, position, floor, fence_token)
            ),
            leased AS ({leased}),
            updated AS (
                UPDATE {checkpoints} c
                SET last_position = GREATEST(c.last_position, x.position),
                    {updateToken}
                    updated_at = now()
                FROM leased x
                WHERE c.processor_id = x.processor_id
                  AND (x.floor IS NULL OR c.last_position >= x.floor)
                  {updateGuard}
                RETURNING c.processor_id
            ),
            inserted AS (
                INSERT INTO {checkpoints} (processor_id, last_position{insertColumns}, updated_at)
                SELECT x.processor_id, x.position{insertValues}, now()
                FROM leased x
                WHERE x.floor IS NULL
                  AND NOT EXISTS (SELECT 1 FROM {checkpoints} c WHERE c.processor_id = x.processor_id)
                ORDER BY x.processor_id
                ON CONFLICT (processor_id) DO UPDATE
                SET last_position = GREATEST({checkpoints}.last_position, EXCLUDED.last_position),
                    {conflictToken}
                    updated_at = now()
                {conflictGuard}
                RETURNING processor_id
            )
            SELECT i.processor_id,
                   EXISTS (SELECT 1 FROM updated u WHERE u.processor_id = i.processor_id)
                       OR EXISTS (SELECT 1 FROM inserted n WHERE n.processor_id = i.processor_id),
                   c.processor_id IS NOT NULL,
                   c.last_position,
                   EXISTS (SELECT 1 FROM leased x WHERE x.processor_id = i.processor_id)
                       AND (NOT @processor_fencing OR c.fence_token IS NULL OR c.fence_token <= i.fence_token)
            FROM input i
            LEFT JOIN {checkpoints} c ON c.processor_id = i.processor_id
            """);

        cmd.Parameters.AddWithValue("processor_ids", writes.Select(w => w.ProcessorId).ToArray());
        cmd.Parameters.AddWithValue("positions", writes.Select(w => w.Position).ToArray());
        cmd.Parameters.AddWithValue("floors", writes.Select(w => w.Floor).ToArray());
        cmd.Parameters.AddWithValue("fence_tokens", writes.Select(w => w.FenceToken).ToArray());
        cmd.Parameters.AddWithValue("processor_fencing", processorFencing);
        if (fencing is not null)
        {
            cmd.Parameters.AddWithValue("consumer_id", fencing.ConsumerId);
            cmd.Parameters.AddWithValue("replica_id", fencing.ReplicaId);
        }

        var floors = writes.ToDictionary(w => w.ProcessorId, w => w.Floor, StringComparer.Ordinal);
        var outcomes = new Dictionary<string, CheckpointWriteOutcome>(writes.Count, StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processorId = reader.GetString(0);
            long? stored = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            var floor = floors[processorId];

            outcomes[processorId] =
                reader.GetBoolean(1) ? new(CheckpointWriteResult.Written)
                : floor is not null && (!reader.GetBoolean(2) || stored < floor) ? new(CheckpointWriteResult.Reset, stored)
                : !reader.GetBoolean(4) ? new(CheckpointWriteResult.Fenced)
                // Leased and above the floor in the snapshot, yet not written: a concurrent
                // writer moved the row in between. Left for the next flush to settle.
                : new(CheckpointWriteResult.Retry);
        }

        return outcomes;
    }
}
