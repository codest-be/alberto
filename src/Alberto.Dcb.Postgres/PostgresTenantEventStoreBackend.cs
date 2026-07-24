using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Multi-tenant PostgreSQL event store backend.
/// Exposes tenant-scoped methods used by <see cref="TenantEventStoreDecorator"/>.
/// Not registered directly as IEventStoreBackend; use .WithTenancy() to enable.
/// </summary>
internal sealed class PostgresTenantEventStoreBackend(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null,
    string? schema = null,
    bool enableStableHeadBarrier = true)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SchemaQualifier _schema = new(schema);
    private readonly bool _enableStableHeadBarrier = enableStableHeadBarrier;
    private readonly string? _schemaName = schema;

    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamForTenant(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = BuildStreamQuery(query);
        await using var cmd = new NpgsqlCommand(sql, connection);

        cmd.Parameters.AddWithValue("p_tenant_id", tenantId);
        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);

        if (query.Types.Count > 0)
        {
            cmd.Parameters.AddWithValue("p_types", query.Types.Select(t => t.Id).ToArray());
        }

        // Handle tag patterns (both exact and wildcards)
        if (query.TagPatterns.Count > 0)
        {
            if (query.HasWildcardPatterns)
            {
                // New function with separate exact tags and prefixes
                var exactTags = query.TagPatterns.Where(p => p.IsExact).Select(p => p.Value).ToArray();
                var prefixes = query.TagPatterns.Where(p => p.IsWildcard).Select(p => p.ConceptPrefix).ToArray();

                cmd.Parameters.AddWithValue("p_exact_tags", exactTags.Length > 0 ? exactTags : DBNull.Value);
                cmd.Parameters.AddWithValue("p_tag_prefixes", prefixes.Length > 0 ? prefixes : DBNull.Value);
            }
            else
            {
                // Legacy function with just exact tags
                cmd.Parameters.AddWithValue("p_tags", query.Tags.Select(t => t.Value).ToArray());
            }
        }

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAllTenants(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM {_schema.Function("alberto_read_all_global")}(@p_after_position, @p_limit)",
            connection);

        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendForTenant(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await AppendCore(connection, null, tenantId, eventsList, dcbQuery, expectedPosition, cancellationToken);
    }

    public async Task<long> GetLastPositionForTenant(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {_schema.Function("alberto_get_last_position")}(@p_tenant_id)",
            connection);

        cmd.Parameters.AddWithValue("p_tenant_id", tenantId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    public async Task<long> GetLastPositionGlobal(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {_schema.Function("alberto_get_last_position_global")}()",
            connection);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    public async Task<IReadOnlyList<long>> GetPositionsGlobalAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT global_position FROM {_schema.Table("alberto_events")} " +
            "WHERE global_position > @after AND global_position <= @ceiling ORDER BY global_position",
            connection);
        cmd.Parameters.AddWithValue("after", afterPosition);
        cmd.Parameters.AddWithValue("ceiling", afterPosition + windowSize);
        var positions = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            positions.Add(reader.GetInt64(0));
        return positions;
    }

    public async Task<long> GetStableHeadGlobalAsync(
        long afterPosition, CancellationToken cancellationToken = default)
    {
        if (!_enableStableHeadBarrier)
            return long.MaxValue;

        // Global (all-tenant) stable head — the subscription axis is global. Clamp
        // the head just below the FIRST position after `afterPosition` that is not
        // yet stable (committed but younger than the oldest running transaction per
        // pg_snapshot_xmin), so it never advances past an in-flight append. Rows with
        // NULL pg_xact_id predate migration 008 and are always stable. Ascending
        // index scan from the head — stops at the first non-stable row.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT global_position FROM {_schema.Table("alberto_events")} " +
            "WHERE global_position > @after " +
            "AND pg_xact_id IS NOT NULL " +
            "AND pg_xact_id >= pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT " +
            "ORDER BY global_position ASC LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue("after", afterPosition);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long firstNonStable ? firstNonStable - 1 : long.MaxValue;
    }

    private async Task<IReadOnlyCollection<IEventEnvelope>> AppendCore(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        var useWildcardFunction = dcbQuery?.HasWildcardPatterns == true;
        var useAllTagsFunction = dcbQuery?.RequiresAllTags == true;
        var useIntersect = dcbQuery?.IntersectsTypesAndTags == true;
        var functionName = useIntersect
            ? (useAllTagsFunction ? "alberto_append_events_v6" : useWildcardFunction ? "alberto_append_events_v5" : "alberto_append_events_v4")
            : (useAllTagsFunction ? "alberto_append_events_v3" : useWildcardFunction ? "alberto_append_events_v2" : "alberto_append_events");
        var sql = useAllTagsFunction
            ? $"SELECT * FROM {_schema.Function(functionName)}(@p_tenant_id, @p_events, @p_dcb_types, @p_dcb_all_tags, @p_expected_position)"
            : useWildcardFunction
                ? $"SELECT * FROM {_schema.Function(functionName)}(@p_tenant_id, @p_events, @p_dcb_types, @p_dcb_exact_tags, @p_dcb_tag_prefixes, @p_expected_position)"
                : $"SELECT * FROM {_schema.Function(functionName)}(@p_tenant_id, @p_events, @p_dcb_types, @p_dcb_tags, @p_expected_position)";

        // #1 Write-skew fix: serialize appends per (schema, tenant) with a
        // transaction-scoped advisory lock so the DCB conflict-check inside the
        // append function and the subsequent insert happen atomically. Per-tenant
        // granularity is sufficient because DCB queries are tenant-scoped — appends
        // to different tenants can never conflict — and preserves cross-tenant
        // append concurrency. Released on commit/rollback.
        NpgsqlTransaction? ownedTransaction =
            transaction is null ? await connection.BeginTransactionAsync(cancellationToken) : null;
        var effectiveTransaction = transaction ?? ownedTransaction!;

        try
        {
            await PostgresEventStoreBackend.AcquireAppendLockAsync(
                connection, effectiveTransaction, $"alberto-append:{_schemaName ?? ""}:{tenantId}", cancellationToken);

            var results = await ExecuteAppendAsync(
                sql, connection, effectiveTransaction, tenantId, eventsList, dcbQuery, expectedPosition,
                useAllTagsFunction, useWildcardFunction, cancellationToken);

            if (ownedTransaction is not null)
                await ownedTransaction.CommitAsync(cancellationToken);

            return results;
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync();
        }
    }

    private async Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAppendAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        bool useAllTagsFunction,
        bool useWildcardFunction,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);

        var eventsJson = BuildEventsJson(eventsList);

        cmd.Parameters.AddWithValue("p_tenant_id", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("p_events", NpgsqlDbType.Jsonb) { Value = eventsJson });

        if (dcbQuery != null && expectedPosition.HasValue)
        {
            cmd.Parameters.AddWithValue("p_dcb_types",
                dcbQuery.Types.Count > 0 ? dcbQuery.Types.Select(t => t.Id).ToArray() : DBNull.Value);

            if (useAllTagsFunction)
            {
                cmd.Parameters.AddWithValue("p_dcb_all_tags",
                    dcbQuery.Tags.Count > 0 ? dcbQuery.Tags.Select(t => t.Value).ToArray() : DBNull.Value);
            }
            else if (useWildcardFunction)
            {
                // v2 function: separate exact tags and prefixes
                var exactTags = dcbQuery.TagPatterns.Where(p => p.IsExact).Select(p => p.Value).ToArray();
                var prefixes = dcbQuery.TagPatterns.Where(p => p.IsWildcard).Select(p => p.ConceptPrefix).ToArray();

                cmd.Parameters.AddWithValue("p_dcb_exact_tags", exactTags.Length > 0 ? exactTags : DBNull.Value);
                cmd.Parameters.AddWithValue("p_dcb_tag_prefixes", prefixes.Length > 0 ? prefixes : DBNull.Value);
            }
            else
            {
                // Legacy function: just exact tags
                cmd.Parameters.AddWithValue("p_dcb_tags",
                    dcbQuery.Tags.Count > 0 ? dcbQuery.Tags.Select(t => t.Value).ToArray() : DBNull.Value);
            }

            cmd.Parameters.AddWithValue("p_expected_position", expectedPosition.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("p_dcb_types", DBNull.Value);

            if (useAllTagsFunction)
            {
                cmd.Parameters.AddWithValue("p_dcb_all_tags", DBNull.Value);
            }
            else if (useWildcardFunction)
            {
                cmd.Parameters.AddWithValue("p_dcb_exact_tags", DBNull.Value);
                cmd.Parameters.AddWithValue("p_dcb_tag_prefixes", DBNull.Value);
            }
            else
            {
                cmd.Parameters.AddWithValue("p_dcb_tags", DBNull.Value);
            }

            cmd.Parameters.AddWithValue("p_expected_position", DBNull.Value);
        }

        try
        {
            var results = new List<IEventEnvelope>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadEventFromAppendResult(reader, tenantId));
            }

            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001" && ex.Message.Contains("DCB conflict"))
        {
            throw new DcbConflictException("DCB conflict detected: events matching the query exist after the expected position", ex);
        }
    }

    private string BuildStreamQuery(DcbQuery query)
    {
        if (query.IsEmpty)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_all")}(@p_tenant_id, @p_after_position, @p_limit)";
        }

        if (query.HasTypesOnly)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_types")}(@p_tenant_id, @p_types, @p_after_position, @p_limit)";
        }

        if (query.RequiresAllTags)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_all_tags")}(@p_tenant_id, @p_tags, @p_after_position, @p_limit)";
            }

            if (query.IntersectsTypesAndTags)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_all_tags")}(@p_tenant_id, @p_types, @p_tags, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_all_tags")}(@p_tenant_id, @p_types, @p_tags, @p_after_position, @p_limit)";
        }

        if (query.HasWildcardPatterns)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_tag_patterns")}(@p_tenant_id, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
            }

            if (query.IntersectsTypesAndTags)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_tag_patterns")}(@p_tenant_id, @p_types, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_tag_patterns")}(@p_tenant_id, @p_types, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
        }

        if (query.HasTagsOnly)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_tags")}(@p_tenant_id, @p_tags, @p_after_position, @p_limit)";
        }

        if (query.IntersectsTypesAndTags)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_tags")}(@p_tenant_id, @p_types, @p_tags, @p_after_position, @p_limit)";
        }

        return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_tags")}(@p_tenant_id, @p_types, @p_tags, @p_after_position, @p_limit)";
    }

    private static string BuildEventsJson(List<IEventToPersist> events)
    {
        var eventsArray = events.Select(e => new
        {
            event_id = e.Id,
            event_type = e.EventType.Id,
            event_tags = e.Tags.Select(t => t.Value).ToArray(),
            event_data = JsonDocument.Parse(e.EventData).RootElement,
            event_metadata = e.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        });

        return JsonSerializer.Serialize(eventsArray);
    }

    private static async Task<List<IEventEnvelope>> ReadEventsAsync(
        NpgsqlCommand cmd,
        CancellationToken cancellationToken)
    {
        var results = new List<IEventEnvelope>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEventFromReader(reader));
        }

        return results;
    }

    private static IEventEnvelope ReadEventFromReader(NpgsqlDataReader reader)
    {
        var globalPosition = reader.GetInt64(reader.GetOrdinal("global_position"));
        var tenantId = reader.GetString(reader.GetOrdinal("tenant_id"));
        var eventId = reader.GetGuid(reader.GetOrdinal("event_id"));
        var eventType = reader.GetString(reader.GetOrdinal("event_type"));
        var eventTags = reader.GetFieldValue<string[]>(reader.GetOrdinal("event_tags"));
        var eventData = reader.GetString(reader.GetOrdinal("event_data"));
        var eventMetadata = reader.GetString(reader.GetOrdinal("event_metadata"));
        var createdAt = reader.GetDateTime(reader.GetOrdinal("created_at"));

        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(eventMetadata) ?? [];

        return new EventEnvelope
        {
            Id = eventId,
            TenantId = tenantId,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType),
            Tags = eventTags.Select(EventTag.Parse).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
    }

    private static IEventEnvelope ReadEventFromAppendResult(NpgsqlDataReader reader, string tenantId)
    {
        var globalPosition = reader.GetInt64(reader.GetOrdinal("global_position"));
        var eventId = reader.GetGuid(reader.GetOrdinal("event_id"));
        var eventType = reader.GetString(reader.GetOrdinal("event_type"));
        var eventTags = reader.GetFieldValue<string[]>(reader.GetOrdinal("event_tags"));
        var eventData = reader.GetString(reader.GetOrdinal("event_data"));
        var eventMetadata = reader.GetString(reader.GetOrdinal("event_metadata"));
        var createdAt = reader.GetDateTime(reader.GetOrdinal("created_at"));

        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(eventMetadata) ?? [];

        return new EventEnvelope
        {
            Id = eventId,
            TenantId = tenantId,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType),
            Tags = eventTags.Select(EventTag.Parse).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
    }
}
