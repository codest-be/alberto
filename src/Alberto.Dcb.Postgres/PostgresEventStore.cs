using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Npgsql;
using NpgsqlTypes;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL event store with support for inline projections.
/// Wraps event appends and inline projections in the same database transaction.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly List<IInlineProjection> _inlineProjections = [];

    /// <summary>
    /// Creates a new PostgresEventStore.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    public PostgresEventStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc/>
    public void RegisterInlineProjection<TState, TProjection>(IStateStore<TState> stateStore)
        where TProjection : Projection<TState>, new()
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _inlineProjections.Add(new InlineProjection<TState, TProjection>(stateStore));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        string tenantId,
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Append events within transaction
            var appended = await AppendEventsInTransactionAsync(
                connection, transaction, tenantId, eventsList, dcbQuery, expectedPosition, cancellationToken);

            // 2. Run inline projections within same transaction
            foreach (var projection in _inlineProjections)
            {
                var relevantEvents = appended
                    .Where(e => projection.HandledEventTypes.Contains(e.EventType.Id))
                    .ToList();

                if (relevantEvents.Count > 0)
                {
                    await projection.ProcessAsync(relevantEvents, transaction, cancellationToken);
                }
            }

            // 3. Commit
            await transaction.CommitAsync(cancellationToken);
            return appended;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
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

        if (query.Tags.Count > 0)
        {
            cmd.Parameters.AddWithValue("p_tags", query.Tags.Select(t => t.Value).ToArray());
        }

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamGlobalAsync(
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT * FROM read_all_global(@p_after_position, @p_limit)",
            connection);

        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);

        return await ReadEventsAsync(cmd, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<long> GetLastPositionAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT get_last_position(@p_tenant_id)",
            connection);

        cmd.Parameters.AddWithValue("p_tenant_id", tenantId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    /// <inheritdoc/>
    public async Task<long> GetLastPositionGlobalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT get_last_position_global()",
            connection);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    private static async Task<IReadOnlyCollection<IEventEnvelope>> AppendEventsInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        List<IEventToPersist> events,
        DcbQuery? dcbQuery,
        long? expectedPosition,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT * FROM append_events(@p_tenant_id, @p_events, @p_dcb_types, @p_dcb_tags, @p_expected_position)",
            connection,
            transaction);

        // Build events JSON array
        var eventsJson = BuildEventsJson(events);

        cmd.Parameters.AddWithValue("p_tenant_id", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("p_events", NpgsqlDbType.Jsonb) { Value = eventsJson });

        // DCB query parameters
        if (dcbQuery != null && expectedPosition.HasValue)
        {
            cmd.Parameters.AddWithValue("p_dcb_types",
                dcbQuery.Types.Count > 0 ? dcbQuery.Types.Select(t => t.Id).ToArray() : DBNull.Value);
            cmd.Parameters.AddWithValue("p_dcb_tags",
                dcbQuery.Tags.Count > 0 ? dcbQuery.Tags.Select(t => t.Value).ToArray() : DBNull.Value);
            cmd.Parameters.AddWithValue("p_expected_position", expectedPosition.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("p_dcb_types", DBNull.Value);
            cmd.Parameters.AddWithValue("p_dcb_tags", DBNull.Value);
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

    private static string BuildStreamQuery(DcbQuery query)
    {
        if (query.IsEmpty)
        {
            return "SELECT * FROM read_all(@p_tenant_id, @p_after_position, @p_limit)";
        }

        if (query.HasTypesOnly)
        {
            return "SELECT * FROM read_by_types(@p_tenant_id, @p_types, @p_after_position, @p_limit)";
        }

        if (query.HasTagsOnly)
        {
            return "SELECT * FROM read_by_tags(@p_tenant_id, @p_tags, @p_after_position, @p_limit)";
        }

        // Has both types and tags
        return "SELECT * FROM read_by_types_or_tags(@p_tenant_id, @p_types, @p_tags, @p_after_position, @p_limit)";
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
