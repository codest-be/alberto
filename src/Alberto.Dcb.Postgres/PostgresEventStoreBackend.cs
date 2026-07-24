using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Single-tenant PostgreSQL implementation of the event store backend.
/// Uses PostgreSQL functions with inverted indexes for efficient DCB queries.
/// No tenant scoping — one event store per database/schema.
/// For multi-tenant mode, use .WithTenancy() which wraps this with TenantEventStoreDecorator.
/// </summary>
public sealed class PostgresEventStoreBackend(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null,
    string? schema = null,
    bool enableStableHeadBarrier = true)
    : IEventStoreBackend
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SchemaQualifier _schema = new(schema);
    private readonly bool _enableStableHeadBarrier = enableStableHeadBarrier;

    // Single-tenant: one append lock per store (schema). Serializes all appends
    // for this store so the DCB conflict-check and insert are atomic — see AppendCore.
    private readonly string _appendLockKey = $"alberto-append:{schema ?? ""}";

    public async Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = BuildStreamQuery(query);
        await using var cmd = new NpgsqlCommand(sql, connection);

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
                var exactTags = query.TagPatterns.Where(p => p.IsExact).Select(p => p.Value).ToArray();
                var prefixes = query.TagPatterns.Where(p => p.IsWildcard).Select(p => p.ConceptPrefix).ToArray();

                cmd.Parameters.AddWithValue("p_exact_tags", exactTags.Length > 0 ? exactTags : DBNull.Value);
                cmd.Parameters.AddWithValue("p_tag_prefixes", prefixes.Length > 0 ? prefixes : DBNull.Value);
            }
            else
            {
                cmd.Parameters.AddWithValue("p_tags", query.Tags.Select(t => t.Value).ToArray());
            }
        }

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAll(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT * FROM {_schema.Function("alberto_read_all")}(@p_after_position, @p_limit)",
            connection);

        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> Append(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await AppendCore(connection, null, eventsList, dcbQuery, expectedPosition, cancellationToken);
    }

    private async Task<IReadOnlyCollection<IEventEnvelope>> AppendCore(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        var useWildcardFunction = dcbQuery?.HasWildcardPatterns == true;
        var useAllTagsFunction = dcbQuery?.RequiresAllTags == true;
        var useIntersect = dcbQuery?.IntersectsTypesAndTags == true;
        // v4: intersect with exact tags. v5: intersect with wildcards. v6: intersect with all-tags.
        // v3: union with all-tags. v2: union with wildcards. v1: union with exact tags.
        var functionName = useIntersect
            ? (useAllTagsFunction ? "alberto_append_events_v6" : useWildcardFunction ? "alberto_append_events_v5" : "alberto_append_events_v4")
            : (useAllTagsFunction ? "alberto_append_events_v3" : useWildcardFunction ? "alberto_append_events_v2" : "alberto_append_events");
        var sql = useAllTagsFunction
            ? $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_all_tags, @p_expected_position)"
            : useWildcardFunction
                ? $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_exact_tags, @p_dcb_tag_prefixes, @p_expected_position)"
                : $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_tags, @p_expected_position)";

        // #1 Write-skew fix: serialize appends for this store with a
        // transaction-scoped advisory lock so the DCB conflict-check inside the
        // append function and the subsequent insert happen atomically. Without
        // it, two concurrent appends can both pass the "no event after
        // expectedPosition" check and both commit, violating the boundary.
        // The lock is held for the whole transaction and released on commit/rollback.
        NpgsqlTransaction? ownedTransaction =
            transaction is null ? await connection.BeginTransactionAsync(cancellationToken) : null;
        var effectiveTransaction = transaction ?? ownedTransaction!;

        try
        {
            await AcquireAppendLockAsync(connection, effectiveTransaction, _appendLockKey, cancellationToken);

            var results = await ExecuteAppendAsync(
                sql, connection, effectiveTransaction, eventsList, dcbQuery, expectedPosition,
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

    internal static async Task AcquireAppendLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string lockKey, CancellationToken cancellationToken)
    {
        // Two-argument advisory-lock form (classid 1 = append serialization). This
        // occupies a separate advisory-lock namespace from the single-argument
        // processor-leadership locks, so the two never collide.
        await using var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(1, hashtext(@lock_key))", connection, transaction);
        lockCmd.Parameters.AddWithValue("lock_key", lockKey);
        await lockCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<IEventEnvelope>> ExecuteAppendAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<IEventToPersist> eventsList,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        bool useAllTagsFunction,
        bool useWildcardFunction,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);

        var eventsJson = BuildEventsJson(eventsList);

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
                var exactTags = dcbQuery.TagPatterns.Where(p => p.IsExact).Select(p => p.Value).ToArray();
                var prefixes = dcbQuery.TagPatterns.Where(p => p.IsWildcard).Select(p => p.ConceptPrefix).ToArray();

                cmd.Parameters.AddWithValue("p_dcb_exact_tags", exactTags.Length > 0 ? exactTags : DBNull.Value);
                cmd.Parameters.AddWithValue("p_dcb_tag_prefixes", prefixes.Length > 0 ? prefixes : DBNull.Value);
            }
            else
            {
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
                results.Add(ReadEventFromAppendResult(reader));
            }

            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001" && ex.Message.Contains("DCB conflict"))
        {
            throw new DcbConflictException("DCB conflict detected: events matching the query exist after the expected position", ex);
        }
    }

    public async Task<long> GetLastPosition(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {_schema.Function("alberto_get_last_position")}()",
            connection);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    public async Task<IReadOnlyList<long>> GetPositionsAsync(
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

    public async Task<long> GetStableHeadAsync(
        long afterPosition, CancellationToken cancellationToken = default)
    {
        if (!_enableStableHeadBarrier)
            return long.MaxValue;

        // The head may safely advance through the contiguous prefix of events whose
        // inserting transaction has committed and is older than every currently
        // in-flight transaction (pg_snapshot_xmin). We find the FIRST position after
        // `afterPosition` that is NOT yet stable (committed but younger than the
        // oldest running transaction) and clamp the head just below it. Because the
        // append path assigns global_position and the xid under the same advisory
        // lock, a committed event above an uncommitted one is itself non-stable, so
        // this boundary never advances past an in-flight append. Rows with NULL
        // pg_xact_id predate migration 008 and are always stable. Ascending index
        // scan from the head — stops at the first non-stable row (the recent tail),
        // so it stays cheap even when a long-running transaction pins xmin.
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

    private string BuildStreamQuery(DcbQuery query)
    {
        if (query.IsEmpty)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_all")}(@p_after_position, @p_limit)";
        }

        if (query.HasTypesOnly)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_types")}(@p_types, @p_after_position, @p_limit)";
        }

        if (query.RequiresAllTags)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_all_tags")}(@p_tags, @p_after_position, @p_limit)";
            }

            if (query.IntersectsTypesAndTags)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_all_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_all_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
        }

        if (query.HasWildcardPatterns)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_tag_patterns")}(@p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
            }

            if (query.IntersectsTypesAndTags)
            {
                return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_tag_patterns")}(@p_types, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_tag_patterns")}(@p_types, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
        }

        if (query.HasTagsOnly)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_tags")}(@p_tags, @p_after_position, @p_limit)";
        }

        if (query.IntersectsTypesAndTags)
        {
            return $"SELECT * FROM {_schema.Function("alberto_read_by_types_and_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
        }

        return $"SELECT * FROM {_schema.Function("alberto_read_by_types_or_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
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
            TenantId = null,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType),
            Tags = eventTags.Select(EventTag.Parse).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
    }

    private static IEventEnvelope ReadEventFromAppendResult(NpgsqlDataReader reader)
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
            TenantId = null,
            GlobalPosition = globalPosition,
            EventType = new EventType(eventType),
            Tags = eventTags.Select(EventTag.Parse).ToArray(),
            EventData = eventData,
            Metadata = metadata,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
    }
}
