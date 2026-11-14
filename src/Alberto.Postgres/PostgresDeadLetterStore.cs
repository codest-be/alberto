using Alberto.Subscriptions;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of dead letter storage.
/// </summary>
/// <remarks>
/// The migrated schema is authoritative. Default construction detects and caches whether
/// <c>alberto_events</c> carries <c>tenant_id</c>, so every method uses the same topology.
/// The compatibility constructor's <c>multiTenant</c> flag can assert multi-tenant storage,
/// but never overrides the topology reported by the schema.
/// </remarks>
internal sealed class PostgresDeadLetterStore : IClaimableDeadLetterStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;
    private readonly PostgresStoreTopology _topology;

    /// <summary>Creates an adapter that derives tenancy from the migrated public schema.</summary>
    public PostgresDeadLetterStore(NpgsqlDataSource dataSource)
        : this(dataSource, schema: null, expectedMultiTenant: null)
    {
    }

    /// <summary>Creates an adapter that derives tenancy from the migrated named schema.</summary>
    public PostgresDeadLetterStore(NpgsqlDataSource dataSource, string? schema)
        : this(dataSource, schema, expectedMultiTenant: null)
    {
    }

    /// <summary>
    /// Binary-compatible constructor retained for existing consumers. <paramref name="multiTenant"/>
    /// set to <see langword="true"/> asserts multi-tenant storage. <see langword="false"/> is
    /// treated as the legacy default (auto-detect) because optional arguments are embedded as
    /// <see langword="false"/> in already-compiled callers.
    /// </summary>
    public PostgresDeadLetterStore(
        NpgsqlDataSource dataSource,
        string? schema = null,
        bool multiTenant = false)
        : this(dataSource, schema, expectedMultiTenant: multiTenant ? true : null)
    {
    }

    private PostgresDeadLetterStore(
        NpgsqlDataSource dataSource,
        string? schema,
        bool? expectedMultiTenant)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        _topology = new PostgresStoreTopology(dataSource, schema, expectedMultiTenant);
    }

    /// <inheritdoc />
    public async Task StoreAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var multiTenant = await _topology.IsMultiTenantAsync(conn, cancellationToken);

        // In multi-tenant mode include tenant_id so the column (added by the multi-tenant
        // migration) is populated.  In single-tenant mode the column does not exist, so
        // keep the original column list unchanged.
        //
        // One row per (processor_id, event_id), enforced by migration 006. A repeat failure
        // refreshes the failure and redelivery schedule columns and leaves id, retry_requested
        // and the claim columns alone, so a retry worker that re-dead-letters the event it claimed still owns the row.
        const string refresh = """
            ON CONFLICT (processor_id, event_id) DO UPDATE SET
                event_type      = EXCLUDED.event_type,
                event_data      = EXCLUDED.event_data,
                error_message   = EXCLUDED.error_message,
                stack_trace     = EXCLUDED.stack_trace,
                attempt_count   = EXCLUDED.attempt_count,
                failed_at       = EXCLUDED.failed_at,
                global_position = EXCLUDED.global_position,
                next_retry_at   = EXCLUDED.next_retry_at,
                redelivery_count = EXCLUDED.redelivery_count
            """;
        string sql;
        if (multiTenant)
        {
            sql = $"""
                INSERT INTO {_schema.Table("alberto_dead_letter_events")}
                    (id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested, next_retry_at, redelivery_count, tenant_id)
                VALUES (@id, @processorId, @eventId, @eventType, @eventData::jsonb, @errorMessage, @stackTrace, @attemptCount, @failedAt, @globalPosition, FALSE, @nextRetryAt, @redeliveryCount, @tenantId)
                {refresh},
                    tenant_id   = EXCLUDED.tenant_id
                """;
        }
        else
        {
            sql = $"""
                INSERT INTO {_schema.Table("alberto_dead_letter_events")}
                    (id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested, next_retry_at, redelivery_count)
                VALUES (@id, @processorId, @eventId, @eventType, @eventData::jsonb, @errorMessage, @stackTrace, @attemptCount, @failedAt, @globalPosition, FALSE, @nextRetryAt, @redeliveryCount)
                {refresh}
                """;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("processorId", entry.ProcessorId);
        cmd.Parameters.AddWithValue("eventId", entry.EventId);
        cmd.Parameters.AddWithValue("eventType", entry.EventType);
        cmd.Parameters.AddWithValue("eventData", entry.EventData);
        cmd.Parameters.AddWithValue("errorMessage", entry.ErrorMessage);
        cmd.Parameters.AddWithValue("stackTrace", (object?)entry.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("attemptCount", entry.AttemptCount);
        cmd.Parameters.AddWithValue("failedAt", entry.FailedAt);
        cmd.Parameters.AddWithValue("globalPosition", entry.GlobalPosition);
        cmd.Parameters.Add(new NpgsqlParameter("nextRetryAt", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = entry.NextRetryAt is { } next ? next.ToUniversalTime() : DBNull.Value
        });
        cmd.Parameters.AddWithValue("redeliveryCount", entry.RedeliveryCount);
        if (multiTenant)
            cmd.Parameters.Add(new NpgsqlParameter("tenantId", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)entry.TenantId ?? DBNull.Value
            });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadLetterEntry>> GetAsync(
        string processorId,
        string? tenantId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var multiTenant = await _topology.IsMultiTenantAsync(conn, cancellationToken);

        // The tenant_id column only exists under the multi-tenant migration. In single-tenant
        // mode there is nothing to select or filter on, and a supplied tenantId is meaningless.
        var tenantSelect = multiTenant ? "tenant_id" : "NULL::text AS tenant_id";
        var tenantFilter = multiTenant && tenantId is not null
            ? "AND tenant_id = @tenantId"
            : string.Empty;

        var sql = $"""
            SELECT id, processor_id, event_id, event_type, event_data, error_message, stack_trace, attempt_count, failed_at, global_position, retry_requested, {tenantSelect}, next_retry_at, redelivery_count
            FROM {_schema.Table("alberto_dead_letter_events")}
            WHERE processor_id = @processorId
            {tenantFilter}
            ORDER BY failed_at DESC
            LIMIT @limit
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("limit", limit);
        if (tenantFilter.Length > 0)
            cmd.Parameters.AddWithValue("tenantId", tenantId!);

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new DeadLetterEntry
            {
                Id = reader.GetGuid(0),
                ProcessorId = reader.GetString(1),
                EventId = reader.GetGuid(2),
                EventType = reader.GetString(3),
                EventData = reader.GetString(4),
                ErrorMessage = reader.GetString(5),
                StackTrace = reader.IsDBNull(6) ? null : reader.GetString(6),
                AttemptCount = reader.GetInt32(7),
                FailedAt = reader.GetFieldValue<DateTimeOffset>(8),
                GlobalPosition = reader.GetInt64(9),
                RetryRequested = reader.GetBoolean(10),
                TenantId = reader.IsDBNull(11) ? null : reader.GetString(11),
                NextRetryAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                RedeliveryCount = reader.GetInt32(13),
            });
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string processorId, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT COUNT(*) FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await _topology.IsMultiTenantAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteRetryAsync(
        DeadLetterClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var sql = $"""
            DELETE FROM {_schema.Table("alberto_dead_letter_events")}
            WHERE id = @id
              AND claim_id = @claim_id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await _topology.IsMultiTenantAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", claim.Entry.Id);
        cmd.Parameters.AddWithValue("claim_id", claim.Token);

        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task ClearAsync(string processorId, CancellationToken cancellationToken = default)
    {
        var sql = $"DELETE FROM {_schema.Table("alberto_dead_letter_events")} WHERE processor_id = @processorId";

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await _topology.IsMultiTenantAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkForRetryAsync(string processorId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_schema.Table("alberto_dead_letter_events")}
            SET retry_requested = TRUE
            WHERE processor_id = @processorId
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await _topology.IsMultiTenantAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterClaim>> ClaimRetryRequestedAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        CancellationToken cancellationToken = default)
        => ClaimCoreAsync(processorId, batchSize, leaseDuration, claimedBy, dueAt: null, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetterClaim>> ClaimDueAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ClaimCoreAsync(processorId, batchSize, leaseDuration, claimedBy, dueAt: now, cancellationToken);

    private async Task<IReadOnlyList<DeadLetterClaim>> ClaimCoreAsync(
        string processorId,
        int batchSize,
        TimeSpan leaseDuration,
        string claimedBy,
        DateTimeOffset? dueAt,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var sql = await BuildClaimSqlAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("batchSize", batchSize);
        cmd.Parameters.AddWithValue("leaseSeconds", leaseDuration.TotalSeconds);
        cmd.Parameters.AddWithValue("claimedBy", claimedBy);
        cmd.Parameters.AddWithValue("claimId", Guid.NewGuid());
        // The application's clock, not the database's: a null now only claims flagged rows.
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = dueAt is { } due ? due.ToUniversalTime() : DBNull.Value
        });

        var claims = new List<DeadLetterClaim>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // Parse tags from array
            IReadOnlyCollection<string>? tags = null;
            if (!reader.IsDBNull(16))
            {
                var tagsArray = reader.GetFieldValue<string[]>(16);
                tags = tagsArray ?? [];
            }

            // Parse metadata from JSONB
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!reader.IsDBNull(17))
            {
                var metadataJson = reader.GetString(17);
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize(metadataJson, AlbertoPostgresJsonContext.Default.DictionaryStringString);
                    metadata = parsed ?? new Dictionary<string, string>();
                }
                catch
                {
                    metadata = new Dictionary<string, string>();
                }
            }

            var expiresAt = reader.GetFieldValue<DateTimeOffset>(12);
            var claimId = reader.GetGuid(14);
            var entry = new DeadLetterEntry
            {
                Id = reader.GetGuid(0),
                ProcessorId = reader.GetString(1),
                EventId = reader.GetGuid(2),
                EventType = reader.GetString(3),
                EventData = reader.GetString(4),
                ErrorMessage = reader.GetString(5),
                StackTrace = reader.IsDBNull(6) ? null : reader.GetString(6),
                AttemptCount = reader.GetInt32(7),
                FailedAt = reader.GetFieldValue<DateTimeOffset>(8),
                GlobalPosition = reader.GetInt64(9),
                RetryRequested = reader.GetBoolean(10),
                TenantId = reader.IsDBNull(15) ? null : reader.GetString(15),
                Tags = tags ?? Array.Empty<string>(),
                Metadata = metadata ?? new Dictionary<string, string>(),
                CreatedAt = reader.IsDBNull(18) ? null : reader.GetDateTime(18),
                ClaimedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                ClaimExpiresAt = expiresAt,
                ClaimedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
                ClaimId = claimId,
                NextRetryAt = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
                RedeliveryCount = reader.GetInt32(20),
            };

            claims.Add(new DeadLetterClaim(entry, claimId, expiresAt));
        }

        return claims;
    }

    /// <inheritdoc />
    public async Task<bool> AbandonRetryAsync(
        DeadLetterClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var sql = $"""
            UPDATE {_schema.Table("alberto_dead_letter_events")}
            SET retry_requested = FALSE,
                -- Still the schedule the claim was made on: nothing rescheduled the row, so
                -- leaving it due would hand it straight back to the next poll. A failure
                -- re-recorded during the retry wrote a new schedule, which is kept.
                next_retry_at = CASE WHEN next_retry_at IS NOT DISTINCT FROM @claimed_next_retry_at
                                     THEN NULL ELSE next_retry_at END,
                claimed_at = NULL,
                claim_expires_at = NULL,
                claimed_by = NULL,
                claim_id = NULL
            WHERE id = @id
              AND claim_id = @claim_id
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await _topology.IsMultiTenantAsync(conn, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", claim.Entry.Id);
        cmd.Parameters.AddWithValue("claim_id", claim.Token);

        cmd.Parameters.Add(new NpgsqlParameter("claimed_next_retry_at", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = claim.Entry.NextRetryAt is { } next ? next.ToUniversalTime() : DBNull.Value
        });

        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Atomically:
    //  1. picks up to @batchSize rows for the processor that are flagged for
    //     retry, or whose next_retry_at is due at @now (the application's clock; a NULL
    //     @now compares as unknown and matches nothing), AND not currently claimed (or the existing claim has expired),
    //     using FOR UPDATE SKIP LOCKED so concurrent workers don't fight,
    //  2. stamps them with claimed_at / claim_expires_at / claimed_by,
    //  3. returns the claimed rows joined with the original event for tags/metadata.
    //
    // The tenant_id column in the SELECT comes from alberto_events (not dead_letter_events)
    // because it reflects the event's origin tenant.  The probe result is cached so the
    // catalog is not queried on every call (SQL-4).
    private async Task<string> BuildClaimSqlAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        var hasTenantId = await _topology.IsMultiTenantAsync(conn, cancellationToken);
        var tenantSelect = hasTenantId ? "e.tenant_id" : "NULL::text AS tenant_id";

        return $"""
            WITH candidates AS (
                SELECT id
                FROM {_schema.Table("alberto_dead_letter_events")}
                WHERE (retry_requested = TRUE OR next_retry_at <= @now)
                  AND processor_id = @processorId
                  AND (claim_expires_at IS NULL OR claim_expires_at < now())
                ORDER BY failed_at ASC
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE {_schema.Table("alberto_dead_letter_events")} dl
                SET claimed_at       = now(),
                    claim_expires_at = now() + (@leaseSeconds || ' seconds')::interval,
                    claimed_by       = @claimedBy,
                    claim_id         = @claimId
                FROM candidates c
                WHERE dl.id = c.id
                RETURNING dl.id, dl.processor_id, dl.event_id, dl.event_type, dl.event_data,
                          dl.error_message, dl.stack_trace, dl.attempt_count, dl.failed_at,
                          dl.global_position, dl.retry_requested,
                          dl.claimed_at, dl.claim_expires_at, dl.claimed_by, dl.claim_id,
                          dl.next_retry_at, dl.redelivery_count
            )
            SELECT
                cl.id, cl.processor_id, cl.event_id, cl.event_type, cl.event_data,
                cl.error_message, cl.stack_trace, cl.attempt_count, cl.failed_at,
                cl.global_position, cl.retry_requested,
                cl.claimed_at, cl.claim_expires_at, cl.claimed_by,
                cl.claim_id, {tenantSelect}, e.event_tags, e.event_metadata, e.created_at,
                cl.next_retry_at, cl.redelivery_count
            FROM claimed cl
            LEFT JOIN {_schema.Table("alberto_events")} e ON cl.event_id = e.event_id
            ORDER BY cl.failed_at ASC
            """;
    }

}
