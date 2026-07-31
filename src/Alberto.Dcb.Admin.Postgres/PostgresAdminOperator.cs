using System.Text.Json;
using Alberto.Dcb.Admin;
using Npgsql;
using NpgsqlTypes;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IAdminOperator"/> that appends an admin audit
/// event to the event log in the same transaction as every state mutation.
///
/// <para>
/// Admin events are inserted with raw SQL rather than routed through <see cref="IEventStoreBackend"/>
/// because the CLI has no DI container and the append pipeline (interceptors, inline projections,
/// post-append handlers) is not needed for audit events. The raw INSERT also lets us share the
/// same <see cref="NpgsqlTransaction"/> as the mutation, guaranteeing atomicity without two-phase
/// commit.
/// </para>
/// <para>
/// Rebuild mutations delegate to <see cref="PostgresProjectionRebuildStore"/> and append the
/// audit event after the store call completes — not in the same transaction, since the store
/// manages its own connection lifecycle.
/// </para>
/// </summary>
public sealed class PostgresAdminOperator : IAdminOperator
{
    /// <summary>
    /// The tenant ID used for admin audit events. Admin events are system-scoped
    /// and do not belong to any application tenant.
    /// </summary>
    internal const string AdminTenantId = "__admin__";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _schemaName;
    private readonly SchemaQualifier _schema;
    private bool? _isMultiTenant;

    /// <summary>
    /// Creates a new PostgresAdminOperator.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name. Can be null for default schema.</param>
    public PostgresAdminOperator(NpgsqlDataSource dataSource, string? schema = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schemaName = schema;
        _schema = new SchemaQualifier(schema);
    }

    /// <inheritdoc />
    public async Task SetCheckpointAsync(string processorId, long position, string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Read current position before the upsert so the audit event captures the delta.
        long fromPosition = 0;
        await using (var readCmd = conn.CreateCommand())
        {
            readCmd.Transaction = tx;
            readCmd.CommandText = $"SELECT last_position FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processorId";
            readCmd.Parameters.AddWithValue("processorId", processorId);
            var result = await readCmd.ExecuteScalarAsync(ct);
            if (result is not DBNull and not null)
                fromPosition = Convert.ToInt64(result);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_processor_checkpoints")} (processor_id, last_position, updated_at)
                VALUES (@processorId, @position, NOW())
                ON CONFLICT (processor_id) DO UPDATE
                  SET last_position = @position, updated_at = NOW()
                """;
            cmd.Parameters.AddWithValue("processorId", processorId);
            cmd.Parameters.AddWithValue("position", position);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var adminEvent = new AdminCheckpointRewound(processorId, fromPosition, position, operatorId);
        await AppendAdminEventAsync(conn, tx, "admin-checkpoint-rewound",
            [new EventTag(AdminTags.Checkpoint, processorId)],
            adminEvent, ct);

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task ResetCheckpointAsync(string processorId, string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {_schema.Table("alberto_processor_checkpoints")} WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var adminEvent = new AdminCheckpointReset(processorId, operatorId);
        await AppendAdminEventAsync(conn, tx, "admin-checkpoint-reset",
            [new EventTag(AdminTags.Checkpoint, processorId)],
            adminEvent, ct);

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CheckpointRenameResult> RenameCheckpointAsync(
        string fromProcessorId, string toProcessorId, string operatorId, CancellationToken ct = default)
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
            // FOR UPDATE so a concurrent rename or checkpoint write cannot move the row out from
            // under the insert-then-delete below.
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
            // DO NOTHING rather than DO UPDATE: silently overwriting a live processor's position
            // would rewind or fast-forward it, so a taken destination is reported, not resolved.
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

            // No audit event and no commit: nothing changed.
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

        var renamed = new AdminCheckpointRenamed(
            fromProcessorId, toProcessorId, sourcePosition.Value, operatorId);
        // Tagged under both ids: the question "what happened to this processor?" has to be
        // answerable from either name, and after the rename the old one is all a reader may have.
        await AppendAdminEventAsync(conn, tx, "admin-checkpoint-renamed",
            [new EventTag(AdminTags.Checkpoint, fromProcessorId), new EventTag(AdminTags.Checkpoint, toProcessorId)],
            renamed, ct);

        await tx.CommitAsync(ct);
        return new CheckpointRenameResult(CheckpointRenameStatus.Renamed, sourcePosition);
    }

    /// <inheritdoc />
    public async Task<int> MarkDeadLettersForRetryAsync(
        string processorId, string operatorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int markedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // retry_requested = FALSE in the predicate so the count is rows this call actually
            // flagged, not rows that were already waiting for the retry loop.
            cmd.CommandText = $"""
                UPDATE {_schema.Table("alberto_dead_letter_events")}
                SET retry_requested = TRUE
                WHERE processor_id = @processorId AND retry_requested = FALSE
                """;
            cmd.Parameters.AddWithValue("processorId", processorId);
            markedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (markedCount > 0)
        {
            var adminEvent = new AdminDeadLettersMarkedForRetry(processorId, markedCount, operatorId);
            await AppendAdminEventAsync(conn, tx, "admin-dead-letters-marked-for-retry",
                [new EventTag(AdminTags.DeadLetter, processorId)],
                adminEvent, ct);
        }

        await tx.CommitAsync(ct);
        return markedCount;
    }

    /// <inheritdoc />
    public async Task<int> ClearAllDeadLettersAsync(string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")}";
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (deletedCount > 0)
        {
            var adminEvent = new AdminDeadLettersCleared(deletedCount, operatorId);
            await AppendAdminEventAsync(conn, tx, "admin-dead-letters-cleared",
                [new EventTag(AdminTags.DeadLetter, "all")],
                adminEvent, ct);
        }

        await tx.CommitAsync(ct);
        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<int> PurgeOutboxAsync(
        DateTimeOffset before, string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Matches OutboxRetentionService's sweep exactly, including the status predicate:
            // an entry that has not been delivered is work, not history, and age does not make
            // it safe to drop.
            cmd.CommandText =
                $"DELETE FROM {_schema.Table("alberto_outbox_entries")} " +
                "WHERE status = 'delivered' AND delivered_at < @before";
            cmd.Parameters.AddWithValue("before", before);
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (deletedCount > 0)
        {
            var adminEvent = new AdminOutboxPurged(before, deletedCount, operatorId);
            await AppendAdminEventAsync(conn, tx, "admin-outbox-purged",
                [new EventTag(AdminTags.Outbox, "purge")],
                adminEvent, ct);
        }

        await tx.CommitAsync(ct);
        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<int> ClearDeadLettersForProcessorAsync(string processorId, string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";
            cmd.Parameters.AddWithValue("processorId", processorId);
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (deletedCount > 0)
        {
            var adminEvent = new AdminDeadLettersCleared(deletedCount, operatorId);
            await AppendAdminEventAsync(conn, tx, "admin-dead-letters-cleared",
                [new EventTag(AdminTags.DeadLetter, processorId)],
                adminEvent, ct);
        }

        await tx.CommitAsync(ct);
        return deletedCount;
    }

    /// <inheritdoc />
    public async Task<RetryByRewindResult> RetryByRewindAsync(
        string processorId, string operatorId, CancellationToken ct = default)
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

        if (earliestPosition is null)
            return new RetryByRewindResult(null, 0);

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

        var adminEvent = new AdminDeadLettersRetried(processorId, rewindPosition, deletedCount, operatorId);
        await AppendAdminEventAsync(conn, tx, "admin-dead-letters-retried",
            [new EventTag(AdminTags.DeadLetter, processorId), new EventTag(AdminTags.Checkpoint, processorId)],
            adminEvent, ct);

        await tx.CommitAsync(ct);
        return new RetryByRewindResult(rewindPosition, deletedCount);
    }

    /// <inheritdoc />
    public async Task<int> ReleaseTenantLeasesAsync(string? consumerId, string operatorId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int deletedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Typed explicitly rather than via AddWithValue: a null consumerId would
            // otherwise reach PostgreSQL as an untyped NULL, and "@consumerId IS NULL"
            // gives the planner nothing to infer a type from — the whole statement fails
            // with 42P08. That is precisely the release-every-lease case, so the default
            // call was the only one that did not work.
            cmd.Parameters.Add(new NpgsqlParameter("consumerId", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)consumerId ?? DBNull.Value,
            });
            cmd.CommandText = $"""
                DELETE FROM {_schema.Table("alberto_tenant_leases")}
                WHERE (@consumerId IS NULL OR consumer_id = @consumerId)
                """;
            deletedCount = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (deletedCount > 0)
        {
            var adminEvent = new AdminTenantLeasesReleased(consumerId, deletedCount, operatorId);
            await AppendAdminEventAsync(conn, tx, "admin-tenant-leases-released",
                [new EventTag(AdminTags.TenantLease, consumerId ?? "all")],
                adminEvent, ct);
        }

        await tx.CommitAsync(ct);
        return deletedCount;
    }

    // -----------------------------------------------------------------------
    // Rebuild operations — delegate to the store, then append audit event
    // separately (the store manages its own connection lifecycle).
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<RebuildStartResult> StartRebuildAsync(
        string processorId, string projectionType, long targetPosition,
        string operatorId, CancellationToken ct = default)
    {
        var store = new PostgresProjectionRebuildStore(_dataSource, _schemaName);
        var state = await store.StartAsync(processorId, projectionType, targetPosition, ct);

        await AppendAdminEventInNewTransactionAsync("admin-rebuild-started",
            [new EventTag(AdminTags.Rebuild, processorId)],
            new AdminRebuildStarted(processorId, projectionType,
                state.RebuildingVersion!.Value, state.TargetPosition!.Value, operatorId),
            ct);

        return new RebuildStartResult(state.ActiveVersion, state.RebuildingVersion!.Value, state.TargetPosition!.Value);
    }

    /// <inheritdoc />
    public async Task<RebuildPromoteResult> PromoteRebuildAsync(
        string processorId, bool force, string operatorId, CancellationToken ct = default)
    {
        var store = new PostgresProjectionRebuildStore(_dataSource, _schemaName);
        var state = await store.RequestPromotionAsync(processorId, force, ct);

        await AppendAdminEventInNewTransactionAsync("admin-rebuild-promoted",
            [new EventTag(AdminTags.Rebuild, processorId)],
            new AdminRebuildPromoted(processorId, state.Status.ToString(), operatorId),
            ct);

        return new RebuildPromoteResult(
            processorId, state.Status.ToString(), state.ActiveVersion, state.RebuildingVersion);
    }

    /// <inheritdoc />
    public async Task<RebuildAbortResult> AbortRebuildAsync(
        string processorId, string operatorId, CancellationToken ct = default)
    {
        var store = new PostgresProjectionRebuildStore(_dataSource, _schemaName);
        var state = await store.RequestAbortAsync(processorId, ct);

        await AppendAdminEventInNewTransactionAsync("admin-rebuild-aborted",
            [new EventTag(AdminTags.Rebuild, processorId)],
            new AdminRebuildAborted(processorId, state.Status.ToString(), operatorId),
            ct);

        return new RebuildAbortResult(
            processorId, state.Status.ToString(), state.ActiveVersion, state.RebuildingVersion);
    }

    // -----------------------------------------------------------------------
    // Shared: append an admin audit event within an existing transaction
    // -----------------------------------------------------------------------

    private async Task AppendAdminEventAsync<TEvent>(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string eventType,
        EventTag[] tags,
        TEvent adminEvent,
        CancellationToken ct)
        where TEvent : IEvent
    {
        var multiTenant = await IsMultiTenantAsync(conn, tx, ct);
        var eventId = Guid.CreateVersion7();
        var eventData = JsonSerializer.Serialize(adminEvent);
        var tagStrings = tags.Select(t => t.Value).ToArray();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        if (multiTenant)
        {
            cmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_events")}
                    (tenant_id, event_id, event_type, event_tags, event_data, event_metadata, created_at)
                VALUES
                    (@tenant_id, @event_id, @event_type, @event_tags, @event_data, @event_metadata, now())
                RETURNING global_position
                """;
            cmd.Parameters.AddWithValue("tenant_id", AdminTenantId);
        }
        else
        {
            cmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_events")}
                    (event_id, event_type, event_tags, event_data, event_metadata, created_at)
                VALUES
                    (@event_id, @event_type, @event_tags, @event_data, @event_metadata, now())
                RETURNING global_position
                """;
        }

        cmd.Parameters.AddWithValue("event_id", eventId);
        cmd.Parameters.AddWithValue("event_type", eventType);
        cmd.Parameters.AddWithValue("event_tags", tagStrings);
        cmd.Parameters.Add(new NpgsqlParameter("event_data", NpgsqlDbType.Jsonb) { Value = eventData });
        cmd.Parameters.Add(new NpgsqlParameter("event_metadata", NpgsqlDbType.Jsonb) { Value = "{}" });

        var globalPosition = (long)(await cmd.ExecuteScalarAsync(ct))!;

        // Maintain the inverted indexes so admin events are discoverable via the
        // same query paths as application events.
        await InsertInvertedIndexesAsync(conn, tx, multiTenant, eventType, tagStrings, globalPosition, ct);
    }

    /// <summary>
    /// Appends an admin audit event in its own connection and transaction.
    /// Used for rebuild operations where the store manages its own connection.
    /// </summary>
    private async Task AppendAdminEventInNewTransactionAsync<TEvent>(
        string eventType,
        EventTag[] tags,
        TEvent adminEvent,
        CancellationToken ct)
        where TEvent : IEvent
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await AppendAdminEventAsync(conn, tx, eventType, tags, adminEvent, ct);
        await tx.CommitAsync(ct);
    }

    private async Task InsertInvertedIndexesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, bool multiTenant,
        string eventType, string[] tagStrings, long globalPosition,
        CancellationToken ct)
    {
        await using var typeCmd = conn.CreateCommand();
        typeCmd.Transaction = tx;

        if (multiTenant)
        {
            typeCmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_event_type_positions")}
                    (tenant_id, event_type, global_position)
                VALUES (@tenant_id, @event_type, @global_position)
                """;
            typeCmd.Parameters.AddWithValue("tenant_id", AdminTenantId);
        }
        else
        {
            typeCmd.CommandText = $"""
                INSERT INTO {_schema.Table("alberto_event_type_positions")}
                    (event_type, global_position)
                VALUES (@event_type, @global_position)
                """;
        }

        typeCmd.Parameters.AddWithValue("event_type", eventType);
        typeCmd.Parameters.AddWithValue("global_position", globalPosition);
        await typeCmd.ExecuteNonQueryAsync(ct);

        foreach (var tag in tagStrings)
        {
            await using var tagCmd = conn.CreateCommand();
            tagCmd.Transaction = tx;

            if (multiTenant)
            {
                tagCmd.CommandText = $"""
                    INSERT INTO {_schema.Table("alberto_event_tag_positions")}
                        (tenant_id, tag, global_position)
                    VALUES (@tenant_id, @tag, @global_position)
                    """;
                tagCmd.Parameters.AddWithValue("tenant_id", AdminTenantId);
            }
            else
            {
                tagCmd.CommandText = $"""
                    INSERT INTO {_schema.Table("alberto_event_tag_positions")}
                        (tag, global_position)
                    VALUES (@tag, @global_position)
                    """;
            }

            tagCmd.Parameters.AddWithValue("tag", tag);
            tagCmd.Parameters.AddWithValue("global_position", globalPosition);
            await tagCmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Detects whether the database schema is multi-tenant (has a <c>tenant_id</c> column
    /// on <c>alberto_events</c>). The result is cached for the lifetime of this instance.
    /// </summary>
    private async Task<bool> IsMultiTenantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (_isMultiTenant.HasValue)
            return _isMultiTenant.Value;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_name = 'alberto_events'
                  AND column_name = 'tenant_id'
                  AND table_schema = @schema
            )
            """;
        cmd.Parameters.AddWithValue("schema",
            string.IsNullOrWhiteSpace(_schemaName) ? "public" : _schemaName);

        _isMultiTenant = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        return _isMultiTenant.Value;
    }
}
