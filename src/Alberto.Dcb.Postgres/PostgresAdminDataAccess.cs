using Npgsql;

namespace Alberto.Dcb.Postgres;

// ---------------------------------------------------------------------------
// Admin-scope result records (read-only projections of DB rows)
// ---------------------------------------------------------------------------

/// <summary>
/// Summary of a processor's checkpoint position as seen from the admin surface.
/// </summary>
public record ProcessorInfo(string ProcessorId, long LastPosition, DateTimeOffset? UpdatedAt);

/// <summary>
/// Checkpoint row for a single processor.
/// </summary>
public record CheckpointInfo(string ProcessorId, long LastPosition, DateTimeOffset? UpdatedAt);

/// <summary>
/// Dead letter entry summary returned by admin inspection queries.
/// </summary>
public record DeadLetterInfo(
    Guid Id,
    string ProcessorId,
    string? EventType,
    long? GlobalPosition,
    string? ErrorMessage,
    DateTimeOffset? FailedAt,
    string? TenantId);

/// <summary>
/// Event summary returned by admin inspection queries.
/// </summary>
public record EventInfo(
    long GlobalPosition,
    string EventType,
    string? Tags,
    DateTimeOffset? CreatedAt,
    string? TenantId);

/// <summary>
/// Aggregate system stats.
/// </summary>
public record SystemInfo(
    long? GlobalPosition,
    long ProcessorCount,
    long DeadLetterCount,
    DateTimeOffset? LastEventAt);

/// <summary>
/// Projection state row.
/// </summary>
public record ProjectionState(
    string DocumentId,
    string? TenantId,
    DateTimeOffset? UpdatedAt);

/// <summary>The tenancy shape of the migrated PostgreSQL store.</summary>
public enum AdminTenancyMode
{
    /// <summary>The schema contains no tenant columns or tenant lease table.</summary>
    SingleTenant,

    /// <summary>The schema stores tenant identity and tenant leases.</summary>
    MultiTenant,
}

/// <summary>Topology facts that admin inspection absorbs on behalf of callers.</summary>
public sealed record AdminStoreTopology(AdminTenancyMode TenancyMode)
{
    /// <summary>Whether tenant-aware filters and result fields are available.</summary>
    public bool IsMultiTenant => TenancyMode is AdminTenancyMode.MultiTenant;
}

/// <summary>
/// Admin view of a tenant lease row from the multi-tenant lease table.
/// Distinct from <c>Alberto.Dcb.Subscriptions.TenantLease</c> (the domain record) —
/// this carries the full admin surface including consumer and replica IDs.
/// </summary>
public record AdminTenantLease(
    string TenantId,
    string ConsumerId,
    string? ReplicaId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Tenant lease inventory together with the store topology that gives an empty list meaning.
/// </summary>
public sealed record TenantLeaseInventory(
    AdminTenancyMode TenancyMode,
    IReadOnlyList<AdminTenantLease> Leases);

/// <summary>
/// An active processor lease found via admin inspection.
/// </summary>
public record ActiveProcessorLease(string ConsumerId, string? ReplicaId, DateTimeOffset ExpiresAt);

/// <summary>The outcome of an atomic checkpoint rename.</summary>
public enum CheckpointRenameStatus
{
    /// <summary>The source checkpoint was moved to the destination ID.</summary>
    Renamed,

    /// <summary>No checkpoint exists under the source ID.</summary>
    SourceNotFound,

    /// <summary>The destination ID already owns a checkpoint and was not overwritten.</summary>
    DestinationExists,

    /// <summary>The source and destination IDs are identical.</summary>
    SameProcessorId,
}

/// <summary>Result of an atomic checkpoint rename.</summary>
public sealed record CheckpointRenameResult(
    CheckpointRenameStatus Status,
    long? Position = null);

// ---------------------------------------------------------------------------
// PostgresAdminDataAccess
// ---------------------------------------------------------------------------

/// <summary>
/// PostgreSQL-specific admin data access: inspection queries and composite operator mutations
/// that have no natural home on a per-processor interface.
///
/// <para>
/// Inspection queries (read-only) cover the full event-store schema and cannot be expressed
/// through the <c>ICheckpointStore</c> / <c>IDeadLetterStore</c> per-processor interfaces.
/// </para>
/// <para>
/// Composite mutations (<see cref="RetryByRewindAsync"/> and <see cref="ReleaseTenantLeasesAsync"/>)
/// are transactional operations that span multiple tables and cannot be composed from individual
/// interface methods without sacrificing atomicity — each interface method opens its own connection.
/// </para>
/// </summary>
public sealed class PostgresAdminDataAccess
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;
    private readonly PostgresStoreTopology _storeTopology;

    /// <summary>
    /// Creates a new PostgresAdminDataAccess.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name. Can be null for default schema.</param>
    public PostgresAdminDataAccess(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        _storeTopology = new PostgresStoreTopology(dataSource, schema);
    }

    // -----------------------------------------------------------------------
    // Inspection queries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the current maximum global position from the event log.
    /// Returns <see langword="null"/> when no events have been appended.
    /// </summary>
    public async Task<long?> GetGlobalPositionAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX(global_position) FROM {_schema.Table("alberto_events")}";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// Lists all processor checkpoints ordered by processor ID.
    /// </summary>
    public async Task<List<ProcessorInfo>> GetProcessorsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema.Table("alberto_processor_checkpoints")}
            ORDER BY processor_id
            """;

        var result = new List<ProcessorInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ProcessorInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    /// <summary>
    /// Lists all processor checkpoints ordered by processor ID.
    /// </summary>
    public async Task<List<CheckpointInfo>> GetCheckpointsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema.Table("alberto_processor_checkpoints")}
            ORDER BY processor_id
            """;

        var result = new List<CheckpointInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CheckpointInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    /// <summary>
    /// Gets the checkpoint for a single processor, or <see langword="null"/> if not found.
    /// </summary>
    public async Task<CheckpointInfo?> GetSingleCheckpointAsync(string processorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT processor_id, last_position, updated_at
            FROM {_schema.Table("alberto_processor_checkpoints")}
            WHERE processor_id = @processorId
            """;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new CheckpointInfo(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
        }

        return null;
    }

    /// <summary>
    /// Atomically moves a checkpoint from <paramref name="fromProcessorId"/> to
    /// <paramref name="toProcessorId"/>.
    /// The destination is never overwritten, and any non-success result leaves both rows unchanged.
    /// </summary>
    public async Task<CheckpointRenameResult> RenameCheckpointAsync(
        string fromProcessorId,
        string toProcessorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromProcessorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toProcessorId);

        if (string.Equals(fromProcessorId, toProcessorId, StringComparison.Ordinal))
            return new CheckpointRenameResult(CheckpointRenameStatus.SameProcessorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? sourcePosition;
        await using (var source = conn.CreateCommand())
        {
            source.Transaction = tx;
            source.CommandText = $"""
                SELECT last_position
                FROM {_schema.Table("alberto_processor_checkpoints")}
                WHERE processor_id = @fromProcessorId
                FOR UPDATE
                """;
            source.Parameters.AddWithValue("fromProcessorId", fromProcessorId);
            var result = await source.ExecuteScalarAsync(ct);
            sourcePosition = result is DBNull or null ? null : Convert.ToInt64(result);
        }

        if (sourcePosition is null)
            return new CheckpointRenameResult(CheckpointRenameStatus.SourceNotFound);

        int inserted;
        await using (var destination = conn.CreateCommand())
        {
            destination.Transaction = tx;
            destination.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_processor_checkpoints")}
                    (processor_id, last_position, updated_at)
                VALUES (@toProcessorId, @position, now())
                ON CONFLICT (processor_id) DO NOTHING
                """;
            destination.Parameters.AddWithValue("toProcessorId", toProcessorId);
            destination.Parameters.AddWithValue("position", sourcePosition.Value);
            inserted = await destination.ExecuteNonQueryAsync(ct);
        }

        if (inserted == 0)
        {
            long? destinationPosition;
            await using var existing = conn.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText = $"""
                SELECT last_position
                FROM {_schema.Table("alberto_processor_checkpoints")}
                WHERE processor_id = @toProcessorId
                """;
            existing.Parameters.AddWithValue("toProcessorId", toProcessorId);
            var result = await existing.ExecuteScalarAsync(ct);
            destinationPosition = result is DBNull or null ? null : Convert.ToInt64(result);

            return new CheckpointRenameResult(
                CheckpointRenameStatus.DestinationExists,
                destinationPosition);
        }

        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = $"""
                DELETE FROM {_schema.Table("alberto_processor_checkpoints")}
                WHERE processor_id = @fromProcessorId
                """;
            delete.Parameters.AddWithValue("fromProcessorId", fromProcessorId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new CheckpointRenameResult(CheckpointRenameStatus.Renamed, sourcePosition);
    }

    /// <summary>
    /// Lists dead letter entries with optional filtering by processor and event type.
    /// Returns at most <paramref name="limit"/> results ordered by <c>failed_at DESC</c>.
    /// </summary>
    public async Task<List<DeadLetterInfo>> GetDeadLettersAsync(
        string? processorId,
        string? type,
        string? tenant,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var topology = await GetTopologyAsync(ct);
        EnsureTenantFilterSupported(topology, tenant);

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
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            where.Add("tenant_id = @tenant");
            cmd.Parameters.AddWithValue("tenant", tenant);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;
        cmd.Parameters.AddWithValue("limit", limit);
        var tenantSelection = topology.IsMultiTenant ? "tenant_id" : "NULL::text AS tenant_id";

        cmd.CommandText = $"""
            SELECT id, processor_id, event_type, global_position, error_message, failed_at,
                   {tenantSelection}
            FROM {_schema.Table("alberto_dead_letter_events")}
            {whereClause}
            ORDER BY failed_at DESC
            LIMIT @limit
            """;

        var result = new List<DeadLetterInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DeadLetterInfo(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    /// <summary>
    /// Lists events from the event log with optional filtering by type and tag.
    /// Returns at most <paramref name="limit"/> results after <paramref name="afterPosition"/>.
    /// </summary>
    public async Task<List<EventInfo>> GetEventsAsync(
        string? type,
        string? tag,
        string? tenant,
        long afterPosition,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPosition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var topology = await GetTopologyAsync(ct);
        EnsureTenantFilterSupported(topology, tenant);

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
            where.Add("@tag = ANY(event_tags)");
            cmd.Parameters.AddWithValue("tag", tag);
        }
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            where.Add("tenant_id = @tenant");
            cmd.Parameters.AddWithValue("tenant", tenant);
        }

        cmd.Parameters.AddWithValue("limit", limit);
        var tenantSelection = topology.IsMultiTenant ? "tenant_id" : "NULL::text AS tenant_id";

        cmd.CommandText = $"""
            SELECT global_position, event_type, array_to_string(event_tags, ','), created_at,
                   {tenantSelection}
            FROM {_schema.Table("alberto_events")}
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
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    /// <summary>
    /// Returns aggregate system stats: current global position, processor count,
    /// total dead letter count, and timestamp of the most recent event.
    /// </summary>
    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        long? globalPosition = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT MAX(global_position) FROM {_schema.Table("alberto_events")}";
            var result = await cmd.ExecuteScalarAsync(ct);
            globalPosition = result is DBNull or null ? null : Convert.ToInt64(result);
        }

        long processorCount = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema.Table("alberto_processor_checkpoints")}";
            var result = await cmd.ExecuteScalarAsync(ct);
            processorCount = Convert.ToInt64(result);
        }

        long deadLetterCount = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {_schema.Table("alberto_dead_letter_events")}";
            var result = await cmd.ExecuteScalarAsync(ct);
            deadLetterCount = Convert.ToInt64(result);
        }

        DateTimeOffset? lastEventAt = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT created_at FROM {_schema.Table("alberto_events")} ORDER BY global_position DESC LIMIT 1";
            var result = await cmd.ExecuteScalarAsync(ct);
            lastEventAt = result is DBNull or null ? null : (DateTimeOffset)result;
        }

        return new SystemInfo(globalPosition, processorCount, deadLetterCount, lastEventAt);
    }

    /// <summary>
    /// Lists all distinct projection types present in the projection-state table.
    /// </summary>
    public async Task<List<string>> GetProjectionTypesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT projection_type FROM {_schema.Table("alberto_projection_states")} ORDER BY 1";

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));

        return result;
    }

    /// <summary>
    /// Lists projection state rows for the given type with optional tenant and substring filters.
    /// Returns at most <paramref name="limit"/> rows ordered by <c>updated_at DESC</c>.
    /// </summary>
    public async Task<List<ProjectionState>> GetProjectionStatesAsync(
        string type,
        string? tenant,
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var topology = await GetTopologyAsync(ct);
        EnsureTenantFilterSupported(topology, tenant);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.Add(new NpgsqlParameter("search", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)search ?? DBNull.Value,
        });
        cmd.Parameters.AddWithValue("limit", limit);

        if (topology.IsMultiTenant)
        {
            cmd.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)tenant ?? DBNull.Value,
            });
            cmd.CommandText = $"""
                SELECT document_id, tenant_id, updated_at
                FROM {_schema.Table("alberto_projection_states")}
                WHERE projection_type = @type
                AND (@tenant IS NULL OR tenant_id = @tenant)
                AND (@search IS NULL OR document_id ILIKE '%' || @search || '%')
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT document_id, NULL::text AS tenant_id, updated_at
                FROM {_schema.Table("alberto_projection_states")}
                WHERE projection_type = @type
                AND (@search IS NULL OR document_id ILIKE '%' || @search || '%')
                ORDER BY updated_at DESC
                LIMIT @limit
                """;
        }

        var result = new List<ProjectionState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ProjectionState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    /// <summary>
    /// Detects and caches whether the migrated store uses the single-tenant or multi-tenant schema.
    /// </summary>
    public async Task<AdminStoreTopology> GetTopologyAsync(CancellationToken ct = default)
    {
        var multiTenant = await _storeTopology.IsMultiTenantAsync(ct);
        return new AdminStoreTopology(
            multiTenant
                ? AdminTenancyMode.MultiTenant
                : AdminTenancyMode.SingleTenant);
    }

    /// <summary>
    /// Lists tenant leases and reports whether an empty list means single-tenant mode or no leases.
    /// Callers do not need a catalog preflight before crossing this interface.
    /// </summary>
    public async Task<TenantLeaseInventory> GetTenantLeaseInventoryAsync(
        CancellationToken ct = default)
    {
        var topology = await GetTopologyAsync(ct);
        if (!topology.IsMultiTenant)
            return new TenantLeaseInventory(topology.TenancyMode, []);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT tenant_id, consumer_id, replica_id, expires_at
            FROM {_schema.Table("alberto_tenant_leases")}
            ORDER BY tenant_id
            """;

        var leases = new List<AdminTenantLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            leases.Add(new AdminTenantLease(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return new TenantLeaseInventory(topology.TenancyMode, leases);
    }

    private static void EnsureTenantFilterSupported(
        AdminStoreTopology topology,
        string? tenant)
    {
        if (!string.IsNullOrWhiteSpace(tenant) && !topology.IsMultiTenant)
        {
            throw new ArgumentException(
                "A tenant filter cannot be applied to a single-tenant Alberto schema.",
                nameof(tenant));
        }
    }

    /// <summary>
    /// Returns the active (non-expired) processor leases held for the given processor ID.
    /// Used as a pre-flight check before operator rewinds to warn when the processor is running.
    /// </summary>
    public async Task<List<ActiveProcessorLease>> GetActiveProcessorLeasesAsync(
        string processorId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT consumer_id, replica_id, expires_at
            FROM {_schema.Table("alberto_processor_leases")}
            WHERE processor_id = @processorId
            AND expires_at > now()
            ORDER BY consumer_id, replica_id
            """;
        cmd.Parameters.AddWithValue("processorId", processorId);

        var result = new List<ActiveProcessorLease>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ActiveProcessorLease(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    /// <summary>
    /// Returns the total count of dead letter entries across all processors.
    /// </summary>
    public async Task<int> CountAllDeadLettersAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_schema.Table("alberto_dead_letter_events")}";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // -----------------------------------------------------------------------
    // Admin mutations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Removes all dead letter entries across every processor.
    /// Returns the number of rows deleted.
    /// </summary>
    public async Task<int> ClearAllDeadLettersAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Releases tenant leases, forcing the application to reacquire them.
    /// When <paramref name="consumerId"/> is non-null, only leases for that consumer group
    /// are released; otherwise all tenant leases are released.
    /// Returns the number of rows deleted.
    /// </summary>
    public async Task<int> ReleaseTenantLeasesAsync(string? consumerId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("consumerId", (object?)consumerId ?? DBNull.Value);
        cmd.CommandText = $"""
            DELETE FROM {_schema.Table("alberto_tenant_leases")}
            WHERE (@consumerId IS NULL OR consumer_id = @consumerId)
            """;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Atomically rewinds a processor checkpoint to one position before its earliest dead letter,
    /// then clears all dead letters for that processor.
    ///
    /// <para>
    /// The three operations (MIN global_position, UPDATE checkpoint, DELETE dead letters) run inside
    /// a single transaction so that a crash mid-way cannot leave the checkpoint rewound without the
    /// dead letters cleared, or vice versa.
    /// </para>
    /// <para>
    /// This method opens its own connection and transaction. It cannot delegate to
    /// <c>ICheckpointStore.RewindAsync</c> or <c>IDeadLetterStore.ClearAsync</c>
    /// because those interfaces open their own connections and cannot participate in a shared
    /// transaction.
    /// </para>
    /// </summary>
    /// <returns>
    /// A tuple of <c>(RewindPosition, DeletedCount)</c> where <c>RewindPosition</c> is the
    /// new checkpoint value and <c>DeletedCount</c> is the number of dead letters removed.
    /// <c>RewindPosition</c> is <see langword="null"/> when the processor has no dead letters:
    /// there is nothing to replay, so the checkpoint is left untouched.
    /// </returns>
    public async Task<(long? RewindPosition, int DeletedCount)> RetryByRewindAsync(
        string processorId,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? earliestPosition;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT MIN(global_position) FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            var result = await cmd.ExecuteScalarAsync(ct);
            earliestPosition = result is DBNull or null ? null : Convert.ToInt64(result);
        }

        // No dead letters means there is nothing to replay. Rewinding anyway would compute
        // position -1 and replay the processor's entire history — a destructive no-op.
        // The guard lives here rather than in the caller so the method cannot be misused.
        if (earliestPosition is null)
            return (null, 0);

        var rewindPosition = earliestPosition.Value - 1;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_processor_checkpoints")} (processor_id, last_position, updated_at)
                VALUES (@processorId, @position, now())
                ON CONFLICT (processor_id) DO UPDATE
                SET last_position = @position,
                    updated_at = now()
                """;
            cmd.Parameters.AddWithValue("processorId", processorId);
            cmd.Parameters.AddWithValue("position", rewindPosition);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        return (rewindPosition, deletedCount);
    }
}
