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
    string? schema = null)
    : IEventStoreBackend
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SchemaQualifier _schema = new(schema);

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
            $"SELECT * FROM {_schema.Function("read_all")}(@p_after_position, @p_limit)",
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
        var functionName = useAllTagsFunction ? "append_events_v3" : useWildcardFunction ? "append_events_v2" : "append_events";
        var sql = useAllTagsFunction
            ? $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_all_tags, @p_expected_position)"
            : useWildcardFunction
                ? $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_exact_tags, @p_dcb_tag_prefixes, @p_expected_position)"
                : $"SELECT * FROM {_schema.Function(functionName)}(@p_events, @p_dcb_types, @p_dcb_tags, @p_expected_position)";

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
            $"SELECT {_schema.Function("get_last_position")}()",
            connection);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    private string BuildStreamQuery(DcbQuery query)
    {
        if (query.IsEmpty)
        {
            return $"SELECT * FROM {_schema.Function("read_all")}(@p_after_position, @p_limit)";
        }

        if (query.HasTypesOnly)
        {
            return $"SELECT * FROM {_schema.Function("read_by_types")}(@p_types, @p_after_position, @p_limit)";
        }

        if (query.RequiresAllTags)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("read_by_all_tags")}(@p_tags, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("read_by_types_or_all_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
        }

        if (query.HasWildcardPatterns)
        {
            if (query.HasTagsOnly)
            {
                return $"SELECT * FROM {_schema.Function("read_by_tag_patterns")}(@p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
            }

            return $"SELECT * FROM {_schema.Function("read_by_types_or_tag_patterns")}(@p_types, @p_exact_tags, @p_tag_prefixes, @p_after_position, @p_limit)";
        }

        if (query.HasTagsOnly)
        {
            return $"SELECT * FROM {_schema.Function("read_by_tags")}(@p_tags, @p_after_position, @p_limit)";
        }

        return $"SELECT * FROM {_schema.Function("read_by_types_or_tags")}(@p_types, @p_tags, @p_after_position, @p_limit)";
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
