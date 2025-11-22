using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.Exceptions;
using Alberto.EventStore.MultiTenant;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Alberto.EventStore.Postgres;

public class PostgresEventStoreBackend : IEventStoreBackend, IMultiTenantEventStore
{
    private readonly int _bulkInsertThreshold;
    private readonly int _commandTimeoutSeconds;
    private readonly string _connectionString;
    private readonly string _eventsTable;
    private readonly ILogger<PostgresEventStoreBackend> _logger;
    private readonly IMetricsRecorder _metrics;
    private readonly PostgresEventStoreOptions _options;

    public PostgresEventStoreBackend(
        IOptions<PostgresEventStoreOptions> options,
        ILogger<PostgresEventStoreBackend> logger,
        IMetricsRecorder? metrics = null)
    {
        _options = options.Value;
        _logger = logger;
        _metrics = metrics ?? new NoopMetricsRecorder();
        _bulkInsertThreshold = _options.BulkInsertThreshold > 0 ? _options.BulkInsertThreshold : 5;
        _connectionString = _options.ConnectionString;
        _eventsTable = $"{_options.Schema}.events";
        _commandTimeoutSeconds = _options.CommandTimeoutSeconds > 0 ? _options.CommandTimeoutSeconds : 30;
    }

    public async Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        Tenant tenant,
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        var hasFilters = query.Tags.Count > 0 || query.EventTypes.Count > 0;
        ValidateQuery(query);
        using var metricsScope = _metrics.RecordQuery(_options.Schema, hasFilters);

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);

        (string sql, DynamicParameters parameters) = BuildStreamQuery(tenant, query, maxCount);

        // Execute query directly with Dapper - no prepared statement caching
        IEnumerable<EventRecord> events = await connection.QueryAsync<EventRecord>(
            CreateCommand(sql, parameters, cancellationToken: cancellationToken));
        var result = events.Select(MapToEventWithMeta).ToList();

        // Record events queried count
        _metrics.RecordEventsQueried(result.Count, _options.Schema, hasFilters);

        return result;
    }

    public async Task<IEnumerable<IEventEnvelope>> Append(
        Tenant tenant,
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken = default)
    {
        List<IEventToPersist> eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        if (consistencyBoundary != null)
            ValidateQuery(consistencyBoundary);

        if (eventsList.Count > 5000)
            _logger.LogWarning("Large batch append detected ({Count} events) for schema {Schema}", eventsList.Count,
                _options.Schema);

        using var metricsScope = _metrics.RecordAppend(tenant.Id, _options.Schema, eventsList.Count);
        try
        {
            IEnumerable<IEventEnvelope> result;
            TransactionContext? ambientContext = TransactionContext.Current;
            if (ambientContext != null)
                result = await ExecuteInAmbientTransaction(
                    tenant,
                    eventsList,
                    consistencyBoundary,
                    expectedLastEventId,
                    ambientContext,
                    cancellationToken);
            else
                result = await ExecuteStandaloneAppend(
                    tenant,
                    eventsList,
                    consistencyBoundary,
                    expectedLastEventId,
                    cancellationToken);

            // Record individual event metrics
            foreach (var evt in eventsList)
            {
                _metrics.RecordEventAppended(tenant.Id, evt.EventType.Id, _options.Schema);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error appending events");
            throw;
        }
    }

    public async Task<IReadOnlyCollection<GlobalEventEnvelope>> StreamAll(
        long fromPosition,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = new StringBuilder($@"
            SELECT position, id, tenant_id, event_type, tags, data as event_data, metadata, created_at
            FROM {_eventsTable}
            WHERE position > @FromPosition");

        var parameters = new DynamicParameters();
        parameters.Add("FromPosition", fromPosition);

        if (eventTypes is { Count: > 0 })
        {
            if (eventTypes.Count == 1)
            {
                sql.Append(" AND event_type = @EventType");
                parameters.Add("EventType", eventTypes.First());
            }
            else
            {
                sql.Append(" AND event_type = ANY(@EventTypes)");
                parameters.Add("EventTypes", eventTypes.ToArray());
            }
        }

        sql.Append(" ORDER BY position LIMIT @MaxCount");
        parameters.Add("MaxCount", maxCount);

        var events = await connection.QueryAsync<EventRecord>(
            CreateCommand(sql.ToString(), parameters, cancellationToken: cancellationToken));

        return events.Select(e => new GlobalEventEnvelope(
            e.position,
            e.id,
            e.tenant_id,
            e.event_type,
            e.event_data,
            DeserializeMetadata(e.metadata),
            e.created_at
        )).ToList();
    }

    private async Task<IEnumerable<IEventEnvelope>> ExecuteInAmbientTransaction(
        Tenant tenant,
        List<IEventToPersist> eventsList,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        TransactionContext ambientContext,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = ambientContext.Connection;
        NpgsqlTransaction transaction = ambientContext.Transaction;

        List<long>? positions = eventsList.Count >= _bulkInsertThreshold
            ? await BulkInsertEventsWithConsistencyCheck(
                eventsList,
                tenant.Id,
                connection,
                transaction,
                consistencyBoundary,
                expectedLastEventId,
                cancellationToken)
            : await InsertEventsSequentiallyWithConsistencyCheck(
                eventsList,
                tenant.Id,
                connection,
                transaction,
                consistencyBoundary,
                expectedLastEventId,
                cancellationToken);

        if (positions == null)
            throw new ConcurrencyConflictException("Consistency boundary has been modified by another process");

        IEnumerable<IEventEnvelope> insertedEvents = CreateInsertedEvents(eventsList, positions);
        return insertedEvents;
    }

    private async Task<IEnumerable<IEventEnvelope>> ExecuteStandaloneAppend(
        Tenant tenant,
        List<IEventToPersist> eventsList,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            using TransactionContext transactionScope = new(connection, transaction);

            try
            {
                IEnumerable<IEventEnvelope> result = await ExecuteInAmbientTransaction(
                    tenant,
                    eventsList,
                    consistencyBoundary,
                    expectedLastEventId,
                    transactionScope,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                return result;
            }
            catch (ConcurrencyConflictException)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception)
        {
            if (transaction.Connection != null)
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                }

            throw;
        }
    }

    private (string sql, DynamicParameters parameters) BuildStreamQuery(Tenant tenant, StreamQuery query, int? maxCount)
    {
        StringBuilder sql = new(
            $@"
            SELECT position, id, tenant_id, event_type, data as event_data, metadata, created_at
            FROM {_eventsTable}
            WHERE tenant_id = @TenantId");

        DynamicParameters parameters = new();
        parameters.Add("TenantId", tenant.Id);

        List<string> conditions = BuildQueryConditions(query, parameters);
        if (conditions.Count > 0)
            sql.Append(" AND (").Append(string.Join(" AND ", conditions)).Append(')');

        sql.Append(" ORDER BY position");

        if (maxCount.HasValue)
        {
            sql.Append(" LIMIT @MaxCount");
            parameters.Add("MaxCount", maxCount.Value);
        }

        return (sql.ToString(), parameters);
    }


    private List<string> BuildQueryConditions(StreamQuery query, DynamicParameters parameters)
    {
        ValidateQuery(query);

        List<string> conditions = [];
        int paramIndex = parameters.ParameterNames.Count();

        if (query.Tags.Count > 0)
        {
            string[] tags = query.Tags.Select(di => di.ToString()).ToArray();
            string op = query.RequireAllTags ? "@>" : "&&";
            parameters.Add($"tags{paramIndex}", tags);
            conditions.Add($"tags {op} @tags{paramIndex}");
            paramIndex++;
        }

        if (query.EventTypes.Count > 0)
        {
            string[] eventTypes = query.EventTypes.Select(et => et.Id).ToArray();
            if (query.RequireAllEventTypes)
            {
                // For single events, requiring ALL event types only makes sense if there's one type
                if (eventTypes.Length == 1)
                {
                    parameters.Add($"eventType{paramIndex}", eventTypes[0]);
                    conditions.Add($"event_type = @eventType{paramIndex}");
                }
                else
                {
                    // Multiple types required for single event is impossible - add impossible condition
                    conditions.Add("FALSE");
                }
            }
            else
            {
                // ANY of the event types can match
                if (eventTypes.Length == 1)
                {
                    parameters.Add($"eventType{paramIndex}", eventTypes[0]);
                    conditions.Add($"event_type = @eventType{paramIndex}");
                }
                else
                {
                    parameters.Add($"eventTypes{paramIndex}", eventTypes);
                    conditions.Add($"event_type = ANY(@eventTypes{paramIndex})");
                }
            }
        }

        return conditions;
    }

    private static void ValidateQuery(StreamQuery query)
    {
        if (query is { RequireAllEventTypes: true, EventTypes.Count: > 1 })
            throw new ArgumentException(
                "RequireAllEventTypes cannot be used with multiple event types in a single event stream query.");
    }

    private async Task<List<long>?> BulkInsertEventsWithConsistencyCheck(
        List<IEventToPersist> eventsList,
        string tenantId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken)
    {
        List<string> valuesClauses = [];
        DynamicParameters parameters = new();
        parameters.Add("TenantId", tenantId);

        for (int i = 0; i < eventsList.Count; i++)
        {
            IEventToPersist evt = eventsList[i];
            Dictionary<string, string> enhancedMetadata = EnhanceMetadataWithTraceContext(evt.Metadata);
            valuesClauses.Add(
                $"(@Id{i}, @TenantId, @EventType{i}, @Tags{i}, @Data{i}::jsonb, @Metadata{i}::jsonb, @CreatedAt{i})");

            parameters.Add($"Id{i}", evt.Id);
            parameters.Add($"EventType{i}", evt.EventType.Id);
            parameters.Add($"Tags{i}", evt.Tags.Select(di => di.ToString()).ToArray());
            parameters.Add($"Data{i}", evt.EventJson);
            parameters.Add($"Metadata{i}", JsonSerializer.Serialize(enhancedMetadata));
            parameters.Add($"CreatedAt{i}", evt.Created.ToUniversalTime());
        }

        string sql;
        if (consistencyBoundary != null)
        {
            (string consistencyConditions, DynamicParameters consistencyParams) = BuildConsistencyConditions(
                consistencyBoundary,
                expectedLastEventId,
                tenantId);

            foreach (string param in consistencyParams.ParameterNames)
                parameters.Add(param, consistencyParams.Get<object>(param));

            // OPTIMIZED: Use CTE to force tenant_id index usage
            if (expectedLastEventId.HasValue)
            {
                sql = $@"
            WITH target_position AS (
                SELECT COALESCE(position, 0) as pos
                FROM {_eventsTable}
                WHERE tenant_id = @TenantId AND id = @ExpectedLastEventId
            ),
            consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 
                        FROM {_eventsTable} e
                        CROSS JOIN target_position tp
                        WHERE {consistencyConditions}
                    ) THEN 1 
                    ELSE 0 
                END as has_conflicts
            ),
            inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                SELECT * FROM (VALUES {string.Join(", ", valuesClauses)}) v
                WHERE (SELECT has_conflicts FROM consistency_check) = 0
                RETURNING position
            )
            SELECT 
                CASE WHEN (SELECT has_conflicts FROM consistency_check) = 1 
                     THEN NULL 
                     ELSE position 
                END as position,
                (SELECT has_conflicts FROM consistency_check) as conflicts
            FROM consistency_check
            LEFT JOIN inserted ON (SELECT has_conflicts FROM consistency_check) = 0
            ORDER BY position";
            }
            else
            {
                sql = $@"
            WITH consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 
                        FROM {_eventsTable} e
                        WHERE {consistencyConditions}
                    ) THEN 1 
                    ELSE 0 
                END as has_conflicts
            ),
            inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                SELECT * FROM (VALUES {string.Join(", ", valuesClauses)}) v
                WHERE (SELECT has_conflicts FROM consistency_check) = 0
                RETURNING position
            )
            SELECT 
                CASE WHEN (SELECT has_conflicts FROM consistency_check) = 1 
                     THEN NULL 
                     ELSE position 
                END as position,
                (SELECT has_conflicts FROM consistency_check) as conflicts
            FROM consistency_check
            LEFT JOIN inserted ON (SELECT has_conflicts FROM consistency_check) = 0
            ORDER BY position";
            }
        }
        else
        {
            sql = $@"
            WITH inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                VALUES {string.Join(", ", valuesClauses)}
                RETURNING position
            )
            SELECT position, 0 as conflicts FROM inserted ORDER BY position";
        }

        try
        {
            _logger.LogDebug("Executing bulk insert with consistency check: {Sql}", sql);
            IEnumerable<dynamic> results = await connection.QueryAsync(
                CreateCommand(sql, parameters, transaction, cancellationToken));
            List<dynamic> resultsList = results.ToList();

            // Check if there were conflicts
            dynamic? firstResult = resultsList.FirstOrDefault();
            if (firstResult != null && (int)firstResult!.conflicts == 1)
            {
                _logger.LogDebug("Consistency boundary conflict detected in bulk insert");
                return null;
            }

            List<long> positions = resultsList.Where(r => r.position != null).Select(r => (long)r.position).ToList();
            return positions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk insert failed, falling back to sequential");
            return await InsertEventsSequentiallyWithConsistencyCheck(
                eventsList,
                tenantId,
                connection,
                transaction,
                consistencyBoundary,
                expectedLastEventId,
                cancellationToken);
        }
    }

    private async Task<List<long>?> InsertEventsSequentiallyWithConsistencyCheck(
        List<IEventToPersist> eventsList,
        string tenantId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken)
    {
        List<long> positions = [];

        foreach (IEventToPersist @event in eventsList)
        {
            long? position = await InsertSingleEventWithConsistencyCheck(
                @event,
                tenantId,
                connection,
                transaction,
                consistencyBoundary,
                expectedLastEventId,
                cancellationToken);

            if (position == null)
                return null; // Consistency check failed

            positions.Add(position.Value);

            // After first successful insert, subsequent inserts don't need consistency check
            // since we're in a transaction and no other process can insert in between
            consistencyBoundary = null;
            expectedLastEventId = null;
        }

        return positions;
    }

    private async Task<long?> InsertSingleEventWithConsistencyCheck(
        IEventToPersist @event,
        string tenantId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> enhancedMetadata = EnhanceMetadataWithTraceContext(@event.Metadata);

        DynamicParameters parameters = new();
        parameters.Add("Id", @event.Id);
        parameters.Add("TenantId", tenantId);
        parameters.Add("EventType", @event.EventType.Id);
        parameters.Add("Tags", @event.Tags.Select(di => di.ToString()).ToArray());
        parameters.Add("Data", @event.EventJson);
        parameters.Add("Metadata", JsonSerializer.Serialize(enhancedMetadata));
        parameters.Add("CreatedAt", @event.Created.ToUniversalTime());

        string sql;
        if (consistencyBoundary != null)
        {
            (string consistencyConditions, DynamicParameters consistencyParams) = BuildConsistencyConditions(
                consistencyBoundary,
                expectedLastEventId,
                tenantId);

            foreach (string param in consistencyParams.ParameterNames)
                parameters.Add(param, consistencyParams.Get<object>(param));

            // OPTIMIZED: Use CTE to force tenant_id index usage
            if (expectedLastEventId.HasValue)
            {
                sql = $@"
            WITH target_position AS (
                SELECT COALESCE(position, 0) as pos
                FROM {_eventsTable}
                WHERE tenant_id = @TenantId AND id = @ExpectedLastEventId
            ),
            consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 
                        FROM {_eventsTable} e
                        CROSS JOIN target_position tp
                        WHERE {consistencyConditions}
                    ) THEN 1 
                    ELSE 0 
                END as has_conflicts
            ),
            inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                SELECT @Id, @TenantId, @EventType, @Tags, @Data::jsonb, @Metadata::jsonb, @CreatedAt
                WHERE (SELECT has_conflicts FROM consistency_check) = 0
                RETURNING position
            )
            SELECT 
                CASE WHEN (SELECT has_conflicts FROM consistency_check) = 1 
                     THEN NULL 
                     ELSE position 
                END as position,
                (SELECT has_conflicts FROM consistency_check) as conflicts
            FROM consistency_check
            LEFT JOIN inserted ON (SELECT has_conflicts FROM consistency_check) = 0";
            }
            else
            {
                sql = $@"
            WITH consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 
                        FROM {_eventsTable} e
                        WHERE {consistencyConditions}
                    ) THEN 1 
                    ELSE 0 
                END as has_conflicts
            ),
            inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                SELECT @Id, @TenantId, @EventType, @Tags, @Data::jsonb, @Metadata::jsonb, @CreatedAt
                WHERE (SELECT has_conflicts FROM consistency_check) = 0
                RETURNING position
            )
            SELECT 
                CASE WHEN (SELECT has_conflicts FROM consistency_check) = 1 
                     THEN NULL 
                     ELSE position 
                END as position,
                (SELECT has_conflicts FROM consistency_check) as conflicts
            FROM consistency_check
            LEFT JOIN inserted ON (SELECT has_conflicts FROM consistency_check) = 0";
            }
        }
        else
        {
            sql = $@"
            INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
            VALUES (@Id, @TenantId, @EventType, @Tags, @Data::jsonb, @Metadata::jsonb, @CreatedAt)
            RETURNING position";
        }

        try
        {
            if (consistencyBoundary != null)
            {
                dynamic? result = await connection.QuerySingleOrDefaultAsync(
                    CreateCommand(sql, parameters, transaction, cancellationToken));
                if (result != null && (int)result!.conflicts == 1)
                    return null;

                return result?.position;
            }

            return await connection.QuerySingleOrDefaultAsync<long?>(
                CreateCommand(sql, parameters, transaction, cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") // Unique constraint violation
        {
            throw new ConcurrencyConflictException($"Event with ID {@event.Id} already exists");
        }
    }

    private (string conditions, DynamicParameters parameters) BuildConsistencyConditions(
        StreamQuery query,
        Guid? expectedLastEventId,
        string tenantId)
    {
        List<string> conditions = [];
        DynamicParameters parameters = new();

        // CRITICAL: Put tenant_id filter FIRST to force index usage
        conditions.Add("e.tenant_id = @TenantId");
        parameters.Add("TenantId", tenantId);

        if (expectedLastEventId.HasValue)
        {
            // Use position from CTE to force tenant_id index usage first
            conditions.Add("e.position > tp.pos");
            parameters.Add("ExpectedLastEventId", expectedLastEventId.Value);
        }
        // else: No position filter when expectedLastEventId is null
        // This allows the planner to use tenant_id index without position constraint

        // Add domain identifier conditions
        if (query.Tags.Count > 0)
        {
            string[] tags = query.Tags.Select(di => di.ToString()).ToArray();
            string op = query.RequireAllTags ? "@>" : "&&";
            parameters.Add("CheckTags", tags);
            conditions.Add($"e.tags {op} @CheckTags");
        }

        // Add event type conditions
        if (query.EventTypes.Count > 0)
        {
            string[] eventTypes = query.EventTypes.Select(et => et.Id).ToArray();
            if (query.RequireAllEventTypes)
            {
                // For single events, requiring ALL event types only makes sense if there's one type
                if (eventTypes.Length == 1)
                {
                    parameters.Add("CheckEventType", eventTypes[0]);
                    conditions.Add("e.event_type = @CheckEventType");
                }
                else
                {
                    // Multiple types required for single event is impossible - add impossible condition
                    conditions.Add("FALSE");
                }
            }
            else
            {
                // ANY of the event types can match
                if (eventTypes.Length == 1)
                {
                    parameters.Add("CheckEventType", eventTypes[0]);
                    conditions.Add("e.event_type = @CheckEventType");
                }
                else
                {
                    parameters.Add("CheckEventTypes", eventTypes);
                    conditions.Add("e.event_type = ANY(@CheckEventTypes)");
                }
            }
        }

        return (string.Join(" AND ", conditions), parameters);
    }

    private static Dictionary<string, string> EnhanceMetadataWithTraceContext(
        IReadOnlyDictionary<string, string> originalMetadata)
    {
        Dictionary<string, string> enhancedMetadata = new(originalMetadata);

        // Capture current trace context
        Activity? currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            enhancedMetadata["traceparent"] = currentActivity.Id ?? "";
            if (!string.IsNullOrEmpty(currentActivity.TraceStateString))
                enhancedMetadata["tracestate"] = currentActivity.TraceStateString;
        }

        return enhancedMetadata;
    }

    private IEnumerable<IEventEnvelope> CreateInsertedEvents(List<IEventToPersist> eventsList, List<long> positions)
    {
        return eventsList.Zip(
            positions,
            IEventEnvelope (eventToPersist, position) =>
            {
                Dictionary<string, string> metadata = new(eventToPersist.Metadata);

                return new EventEnvelope
                {
                    Id = eventToPersist.Id,
                    Position = position,
                    EventType = eventToPersist.EventType,
                    EventJson = eventToPersist.EventJson,
                    Metadata = metadata,
                    Created = eventToPersist.Created
                };
            });
    }

    private static ActivityContext? ExtractTraceContextFromMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("traceparent", out string? traceParent) || string.IsNullOrEmpty(traceParent))
            return null;

        metadata.TryGetValue("tracestate", out string? traceState);

        if (ActivityContext.TryParse(traceParent, traceState, out ActivityContext context))
            return context;

        return null;
    }

    private static IEventEnvelope MapToEventWithMeta(EventRecord record)
    {
        Dictionary<string, string> metadata = string.IsNullOrEmpty(record.metadata)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(record.metadata)
              ?? new Dictionary<string, string>();

        // Extract trace context for potential restoration
        ActivityContext? traceContext = ExtractTraceContextFromMetadata(metadata);
        if (traceContext.HasValue)
            metadata["_trace_context"] = "available"; // Flag that trace context is available

        return new EventEnvelope
        {
            Id = record.id,
            Position = record.position,
            EventType = new EventType(record.event_type),
            EventJson = record.event_data,
            Metadata = metadata,
            Created = record.created_at
        };
    }

    private static Dictionary<string, string> DeserializeMetadata(string metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return new Dictionary<string, string>();

        return JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)
               ?? new Dictionary<string, string>();
    }

    private CommandDefinition CreateCommand(
        string sql,
        object parameters,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        return new CommandDefinition(
            sql,
            parameters,
            transaction: transaction,
            commandTimeout: _commandTimeoutSeconds,
            cancellationToken: cancellationToken);
    }

    // ReSharper disable InconsistentNaming
    private class EventRecord
    {
        public long position { get; set; }
        public Guid id { get; set; }
        public string tenant_id { get; set; } = null!;
        public string event_type { get; set; } = null!;
        public string[] tags { get; set; } = null!;
        public string event_data { get; set; } = null!;
        public string metadata { get; set; } = null!;
        public DateTimeOffset created_at { get; set; }
    }
    // ReSharper restore InconsistentNaming
}

public class TransactionContext : IDisposable
{
    // ReSharper disable once InconsistentNaming
    private static readonly AsyncLocal<TransactionContext?> _current = new();

    public TransactionContext(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
        _current.Value = this;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    public static TransactionContext? Current => _current.Value;

    public void Dispose()
    {
        _current.Value = null;
    }
}