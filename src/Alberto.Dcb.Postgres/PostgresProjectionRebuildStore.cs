using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectionRebuildStore"/>, backed by
/// <c>alberto_projection_rebuild_meta</c>.
/// </summary>
/// <remarks>
/// Operator calls persist intent without performing completion work. Coordinator transitions
/// lock the current row before changing versions. Promotion additionally spans
/// <c>alberto_projection_states</c>, so it runs in a transaction: readers move from a complete
/// old version to a complete new one.
/// </remarks>
public sealed class PostgresProjectionRebuildStore :
    IProjectionRebuildStore,
    IProjectionRebuildCoordinatorStore
{
    private const string Columns =
        "processor_id, projection_type, active_version, rebuilding_version, rebuild_status, " +
        "rebuild_started_at, rebuild_target_position, rebuild_completed_at, " +
        "last_allocated_version, requested_action";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    /// <summary>
    /// Creates a new rebuild store.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name, or null for the default schema.</param>
    public PostgresProjectionRebuildStore(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
    }

    private string MetaTable => _schema.Table("alberto_projection_rebuild_meta");
    private string StatesTable => _schema.Table("alberto_projection_states");

    /// <inheritdoc/>
    public async Task<ProjectionRebuildState> GetAsync(
        string processorId, string projectionType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {MetaTable} WHERE processor_id = @processor_id", conn);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return Map(reader);

        // A processor that has never been rebuilt has no row. Report it as idle at version 1
        // rather than as a missing value, so callers have exactly one shape to handle.
        return new ProjectionRebuildState(
            processorId, projectionType, ActiveVersion: 1, RebuildingVersion: null,
            RebuildStatus.Idle, StartedAt: null, TargetPosition: null, CompletedAt: null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProjectionRebuildState>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {MetaTable} ORDER BY processor_id", conn);

        var results = new List<ProjectionRebuildState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));

        return results;
    }

    /// <inheritdoc/>
    public async Task<ProjectionRebuildState> StartAsync(
        string processorId, string projectionType, long targetPosition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        ArgumentOutOfRangeException.ThrowIfNegative(targetPosition);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // The WHERE on the DO UPDATE branch is what makes this safe under a race: a row
        // already in 'rebuilding' or 'ready' matches nothing, so the second caller gets no
        // row back and throws. The new rebuilding version is derived from the row's own
        // high-water mark inside the same statement, so it cannot be computed from a stale read.
        //
        // It advances last_allocated_version rather than active_version + 1 because abort
        // leaves active_version where it was. Handing the aborted rebuild's number to the next
        // one lets a shadow loop that has not yet noticed the abort seed the fresh replay with
        // its own leftovers, and every event is then applied twice. See migration 015.
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {MetaTable} (
                processor_id, projection_type, active_version, rebuilding_version,
                last_allocated_version, rebuild_status, rebuild_started_at,
                rebuild_target_position, rebuild_completed_at, requested_action, updated_at)
            VALUES (
                @processor_id, @projection_type, 1, 2,
                2, 'rebuilding', now(),
                @target_position, NULL, NULL, now())
            ON CONFLICT (processor_id) DO UPDATE SET
                projection_type         = @projection_type,
                rebuilding_version      = {MetaTable}.last_allocated_version + 1,
                last_allocated_version  = {MetaTable}.last_allocated_version + 1,
                rebuild_status          = 'rebuilding',
                rebuild_started_at      = now(),
                rebuild_target_position = @target_position,
                rebuild_completed_at    = NULL,
                requested_action        = NULL,
                updated_at              = now()
            WHERE {MetaTable}.rebuild_status NOT IN ('rebuilding', 'ready')
            RETURNING {Columns}
            """, conn);

        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("target_position", targetPosition);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return Map(reader);

        throw new RebuildStateException(
            $"Cannot start a rebuild for processor '{processorId}': one is already in flight. " +
            $"Promote it, or abort it first.");
    }

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> RequestPromotionAsync(
        string processorId,
        bool force = false,
        CancellationToken ct = default) =>
        RequestActionAsync(
            processorId,
            force ? RebuildOperatorAction.ForcePromote : RebuildOperatorAction.Promote,
            ct);

    /// <inheritdoc/>
    public Task<ProjectionRebuildState> RequestAbortAsync(
        string processorId,
        CancellationToken ct = default) =>
        RequestActionAsync(processorId, RebuildOperatorAction.Abort, ct);

    private async Task<ProjectionRebuildState> RequestActionAsync(
        string processorId,
        RebuildOperatorAction action,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {MetaTable}
            SET requested_action = @requested_action, updated_at = now()
            WHERE processor_id = @processor_id
              AND rebuild_status IN ('rebuilding', 'ready')
            RETURNING {Columns}
            """, conn);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("requested_action", FormatAction(action));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return Map(reader);

        throw new RebuildStateException(
            $"Cannot request {FormatAction(action)} for processor '{processorId}': " +
            "no rebuild is in flight.");
    }

    /// <inheritdoc/>
    async Task<ProjectionRebuildState> IProjectionRebuildCoordinatorStore.MarkReadyAsync(
        string processorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {MetaTable}
            SET rebuild_status = 'ready', updated_at = now()
            WHERE processor_id = @processor_id AND rebuild_status = 'rebuilding'
            RETURNING {Columns}
            """, conn);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return Map(reader);

        throw new RebuildStateException(
            $"Cannot mark processor '{processorId}' ready: no rebuild is in flight.");
    }

    /// <inheritdoc/>
    async Task<RebuildOutcome> IProjectionRebuildCoordinatorStore.CompletePromotionAsync(
        string processorId, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock and read first. The version being superseded is the *current* active version,
        // and it has to be captured before the flip overwrites it — a subquery in the UPDATE's
        // RETURNING clause would be reading the same row the statement is modifying, which is
        // exactly the case where PostgreSQL's snapshot rules are easy to get wrong.
        var current = await ReadForUpdateAsync(conn, tx, processorId, ct)
            ?? throw new RebuildStateException(
                $"Cannot promote processor '{processorId}': no rebuild has ever been started for it.");

        if (!current.IsRebuildInFlight)
            throw new RebuildStateException(
                $"Cannot promote processor '{processorId}': no rebuild is in flight " +
                $"(status is {current.Status}).");

        if (current.Status is RebuildStatus.Rebuilding && !force)
            throw new RebuildStateException(
                $"Cannot promote processor '{processorId}': the rebuild has not finished " +
                $"replaying (target position {current.TargetPosition}). Wait for it to reach " +
                $"Ready, or use an early-promotion request.");

        await using (var flipCmd = new NpgsqlCommand(
            $"""
            UPDATE {MetaTable}
            SET active_version       = rebuilding_version,
                rebuilding_version   = NULL,
                rebuild_status       = 'completed',
                rebuild_completed_at = now(),
                requested_action     = NULL,
                updated_at           = now()
            WHERE processor_id = @processor_id
            """, conn, tx))
        {
            flipCmd.Parameters.AddWithValue("processor_id", processorId);
            await flipCmd.ExecuteNonQueryAsync(ct);
        }

        // Drop the version being superseded in the same transaction as the flip. That is what
        // makes the swap invisible: readers see the old version complete, then the new version
        // complete, and never a half-deleted one.
        await DeleteStateVersionAsync(conn, tx, current.ProjectionType, current.ActiveVersion, ct);
        await tx.CommitAsync(ct);

        return new RebuildOutcome(
            await GetAsync(processorId, current.ProjectionType, ct), current.ActiveVersion);
    }

    /// <inheritdoc/>
    async Task<RebuildOutcome> IProjectionRebuildCoordinatorStore.CompleteAbortAsync(
        string processorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Same reason as PromoteAsync: the version being discarded has to be read before the
        // UPDATE nulls it out.
        var current = await ReadForUpdateAsync(conn, tx, processorId, ct);

        if (current is null || !current.IsRebuildInFlight)
            throw new RebuildStateException(
                $"Cannot abort a rebuild for processor '{processorId}': none is in flight.");

        // Guaranteed non-null by the version CHECK constraint whenever the status is in flight.
        var abandonedVersion = current.RebuildingVersion!.Value;

        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE {MetaTable}
            SET rebuilding_version   = NULL,
                rebuild_status       = 'aborted',
                rebuild_completed_at = now(),
                requested_action     = NULL,
                updated_at           = now()
            WHERE processor_id = @processor_id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("processor_id", processorId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // The active version is untouched, so discarding the partial rebuild is invisible
        // to readers.
        await DeleteStateVersionAsync(conn, tx, current.ProjectionType, abandonedVersion, ct);
        await tx.CommitAsync(ct);

        return new RebuildOutcome(
            await GetAsync(processorId, current.ProjectionType, ct), abandonedVersion);
    }

    /// <summary>
    /// Reads a processor's row under a row lock, or returns null when it has none.
    /// Holding the lock for the rest of the transaction is what stops a concurrent promote
    /// or abort from moving the version numbers between the validation and the write.
    /// </summary>
    private async Task<ProjectionRebuildState?> ReadForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string processorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {MetaTable} WHERE processor_id = @processor_id FOR UPDATE",
            conn, tx);
        cmd.Parameters.AddWithValue("processor_id", processorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    /// <summary>
    /// Deletes every projection state row for one version of one projection type.
    /// Spans all tenants: a rebuild is a schema-level operation, not a per-tenant one.
    /// </summary>
    private async Task DeleteStateVersionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string projectionType, int version, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {StatesTable}
            WHERE projection_type = @projection_type AND rebuild_version = @version
            """, conn, tx);

        cmd.Parameters.AddWithValue("projection_type", projectionType);
        cmd.Parameters.AddWithValue("version", version);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ProjectionRebuildState Map(NpgsqlDataReader reader) => new(
        ProcessorId: reader.GetString(reader.GetOrdinal("processor_id")),
        ProjectionType: reader.GetString(reader.GetOrdinal("projection_type")),
        ActiveVersion: reader.GetInt32(reader.GetOrdinal("active_version")),
        RebuildingVersion: GetNullableInt(reader, "rebuilding_version"),
        Status: ParseStatus(reader.GetString(reader.GetOrdinal("rebuild_status"))),
        StartedAt: GetNullableTimestamp(reader, "rebuild_started_at"),
        TargetPosition: GetNullableLong(reader, "rebuild_target_position"),
        CompletedAt: GetNullableTimestamp(reader, "rebuild_completed_at"),
        LastAllocatedVersion: reader.GetInt32(reader.GetOrdinal("last_allocated_version")),
        RequestedAction: GetNullableAction(reader, "requested_action"));

    private static int? GetNullableInt(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? GetNullableTimestamp(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static RebuildOperatorAction? GetNullableAction(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : ParseAction(reader.GetString(ordinal));
    }

    private static RebuildStatus ParseStatus(string status) => status switch
    {
        "idle" => RebuildStatus.Idle,
        "rebuilding" => RebuildStatus.Rebuilding,
        "ready" => RebuildStatus.Ready,
        "completed" => RebuildStatus.Completed,
        "aborted" => RebuildStatus.Aborted,
        // The CHECK constraint added in migration 014 makes this unreachable for any database
        // built by the shipped migrations.
        _ => throw new InvalidOperationException(
            $"Unrecognised rebuild status '{status}' in alberto_projection_rebuild_meta."),
    };

    private static string FormatAction(RebuildOperatorAction action) => action switch
    {
        RebuildOperatorAction.Promote => "promote",
        RebuildOperatorAction.ForcePromote => "force-promote",
        RebuildOperatorAction.Abort => "abort",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static RebuildOperatorAction ParseAction(string action) => action switch
    {
        "promote" => RebuildOperatorAction.Promote,
        "force-promote" => RebuildOperatorAction.ForcePromote,
        "abort" => RebuildOperatorAction.Abort,
        _ => throw new InvalidOperationException(
            $"Unrecognised requested action '{action}' in alberto_projection_rebuild_meta."),
    };
}
