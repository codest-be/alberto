using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using EventStore.Events;
using EventStore.Exceptions;
using EventStore.MultiTenant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventStore.Postgres;

public class PostgresEventStoreBackend(
    IOptions<PostgresEventStoreOptions> options,
    ILogger<PostgresEventStoreBackend> logger)
    : IEventStoreBackend
{
    private readonly int _bulkInsertThreshold =
        options.Value.BulkInsertThreshold > 0 ? options.Value.BulkInsertThreshold : 5;

    private readonly string _connectionString = options.Value.ConnectionString;
    private readonly string _eventsTable = $"{options.Value.Schema}.events";

    public async Task<IReadOnlyCollection<IEventEnvelope>> Stream(
        Tenant tenant,
        StreamQuery query,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var (sql, parameters) = BuildStreamQuery(tenant, query, maxCount);
        var events = await connection.QueryAsync<EventRecord>(sql, parameters);

        return events.Select(MapToEventWithMeta).ToList();
    }

    public async Task<IEnumerable<IEventEnvelope>> Append(
        Tenant tenant,
        IEnumerable<IEventToPersist> events,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        try
        {
            var ambientContext = TransactionContext.Current;
            if (ambientContext != null)
                return await ExecuteInAmbientTransaction(
                    tenant,
                    eventsList,
                    consistencyBoundary,
                    expectedLastEventId,
                    ambientContext,
                    cancellationToken);

            return await ExecuteStandaloneAppend(
                tenant,
                eventsList,
                consistencyBoundary,
                expectedLastEventId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error appending events");
            throw;
        }
    }

    private async Task<IEnumerable<IEventEnvelope>> ExecuteInAmbientTransaction(
        Tenant tenant,
        List<IEventToPersist> eventsList,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        TransactionContext ambientContext,
        CancellationToken cancellationToken)
    {
        var connection = ambientContext.Connection;
        var transaction = ambientContext.Transaction;

        var positions = eventsList.Count >= _bulkInsertThreshold
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

        var insertedEvents = CreateInsertedEvents(eventsList, positions);
        return insertedEvents;
    }

    private async Task<IEnumerable<IEventEnvelope>> ExecuteStandaloneAppend(
        Tenant tenant,
        List<IEventToPersist> eventsList,
        StreamQuery? consistencyBoundary,
        Guid? expectedLastEventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            using var transactionScope = new TransactionContext(connection, transaction);

            try
            {
                var result = await ExecuteInAmbientTransaction(
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
        var sql = new StringBuilder(
            $@"
            SELECT position, id, tenant_id, event_type, data as event_data, metadata, created_at
            FROM {_eventsTable}
            WHERE tenant_id = @TenantId");

        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenant.Id);

        var conditions = BuildQueryConditions(query, parameters);
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
        var conditions = new List<string>();
        var paramIndex = parameters.ParameterNames.Count();

        if (query.Tags.Count > 0)
        {
            var tags = query.Tags.Select(di => di.ToString()).ToArray();
            var op = query.RequireAllTags ? "@>" : "&&";
            parameters.Add($"tags{paramIndex}", tags);
            conditions.Add($"tags {op} @tags{paramIndex}");
            paramIndex++;
        }

        if (query.EventTypes.Count > 0)
        {
            var eventTypes = query.EventTypes.Select(et => et.Id).ToArray();
            if (eventTypes.Length == 1)
            {
                parameters.Add($"eventType{paramIndex}", eventTypes[0]);
                conditions.Add($"event_type = @eventType{paramIndex}");
            }
            else if (!query.RequireAllEventTypes)
            {
                parameters.Add($"eventTypes{paramIndex}", eventTypes);
                conditions.Add($"event_type = ANY(@eventTypes{paramIndex})");
            }
        }

        return conditions;
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
        var valuesClauses = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId);

        for (var i = 0; i < eventsList.Count; i++)
        {
            var evt = eventsList[i];
            var enhancedMetadata = EnhanceMetadataWithTraceContext(evt.Metadata);
            valuesClauses.Add(
                $"(@Id{i}, @TenantId, @EventType{i}, @Tags{i}, @Data{i}::jsonb, @Metadata{i}::jsonb, @CreatedAt{i})");

            parameters.Add($"Id{i}", evt.Id);
            parameters.Add($"EventType{i}", evt.EventType.Id);
            parameters.Add($"Tags{i}", evt.Tags.Select(di => di.ToString()).ToArray());
            parameters.Add($"Data{i}", evt.EventJson);
            parameters.Add($"Metadata{i}", JsonSerializer.Serialize(enhancedMetadata));
            parameters.Add($"CreatedAt{i}", evt.Created);
        }

        string sql;
        if (consistencyBoundary != null)
        {
            var (consistencyConditions, consistencyParams) = BuildConsistencyConditions(
                consistencyBoundary,
                expectedLastEventId,
                tenantId);

            foreach (var param in consistencyParams.ParameterNames)
                parameters.Add(param, consistencyParams.Get<object>(param));

            sql = $@"
            WITH consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 FROM {_eventsTable}
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
            WITH inserted AS (
                INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
                VALUES {string.Join(", ", valuesClauses)}
                RETURNING position
            )
            SELECT position, 0 as conflicts FROM inserted ORDER BY position";
        }

        try
        {
            logger.LogDebug("Executing bulk insert with consistency check: {Sql}", sql);
            var results = await connection.QueryAsync(sql, parameters, transaction);
            var resultsList = results.ToList();

            // Check if there were conflicts
            var firstResult = resultsList.FirstOrDefault();
            if (firstResult != null && (int)firstResult!.conflicts == 1)
            {
                logger.LogDebug("Consistency boundary conflict detected in bulk insert");
                return null;
            }

            var positions = resultsList.Where(r => r.position != null).Select(r => (long)r.position).ToList();
            return positions;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bulk insert failed, falling back to sequential");
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
        var positions = new List<long>();

        foreach (var @event in eventsList)
        {
            var position = await InsertSingleEventWithConsistencyCheck(
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
        var enhancedMetadata = EnhanceMetadataWithTraceContext(@event.Metadata);

        var parameters = new DynamicParameters();
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
            var (consistencyConditions, consistencyParams) = BuildConsistencyConditions(
                consistencyBoundary,
                expectedLastEventId,
                tenantId);

            foreach (var param in consistencyParams.ParameterNames)
                parameters.Add(param, consistencyParams.Get<object>(param));

            sql = $@"
            WITH consistency_check AS (
                SELECT CASE 
                    WHEN EXISTS (
                        SELECT 1 FROM {_eventsTable}
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
            INSERT INTO {_eventsTable} (id, tenant_id, event_type, tags, data, metadata, created_at)
            VALUES (@Id, @TenantId, @EventType, @Tags, @Data::jsonb, @Metadata::jsonb, @CreatedAt)
            RETURNING position";
        }

        if (consistencyBoundary != null)
        {
            var result = await connection.QuerySingleOrDefaultAsync(sql, parameters, transaction);
            if (result != null && (int)result!.conflicts == 1)
                return null;

            return result?.position;
        }

        return await connection.QuerySingleOrDefaultAsync<long?>(sql, parameters, transaction);
    }

    private (string conditions, DynamicParameters parameters) BuildConsistencyConditions(
        StreamQuery query,
        Guid? expectedLastEventId,
        string tenantId)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        conditions.Add("tenant_id = @TenantId");
        parameters.Add("TenantId", tenantId);

        if (expectedLastEventId.HasValue)
        {
            // Check for events after the expected last event within the boundary
            conditions.Add(
                $"position > COALESCE((SELECT position FROM {_eventsTable} WHERE tenant_id = @TenantId AND id = @ExpectedLastEventId), -1)"); // Use -1 so if event not found, we check for any events

            parameters.Add("ExpectedLastEventId", expectedLastEventId.Value);
        }
        else
        {
            // If no expected last event, check for any events matching the boundary
            conditions.Add("position >= 0");
        }

        // Add domain identifier conditions
        if (query.Tags.Count > 0)
        {
            var tags = query.Tags.Select(di => di.ToString()).ToArray();
            var op = query.RequireAllTags ? "@>" : "&&";
            parameters.Add("CheckTags", tags);
            conditions.Add($"tags {op} @CheckTags");
        }

        // Add event type conditions
        if (query.EventTypes.Count > 0)
        {
            var eventTypes = query.EventTypes.Select(et => et.Id).ToArray();
            if (eventTypes.Length == 1)
            {
                parameters.Add("CheckEventType", eventTypes[0]);
                conditions.Add("event_type = @CheckEventType");
            }
            else if (!query.RequireAllEventTypes)
            {
                parameters.Add("CheckEventTypes", eventTypes);
                conditions.Add("event_type = ANY(@CheckEventTypes)");
            }
        }

        return (string.Join(" AND ", conditions), parameters);
    }

    private static Dictionary<string, string> EnhanceMetadataWithTraceContext(
        IReadOnlyDictionary<string, string> originalMetadata)
    {
        var enhancedMetadata = new Dictionary<string, string>(originalMetadata);

        // Capture current trace context
        var currentActivity = Activity.Current;
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
            IEventEnvelope (eventToPersist, _) =>
            {
                var metadata = new Dictionary<string, string>(eventToPersist.Metadata);

                return new EventEnvelope
                {
                    Id = eventToPersist.Id,
                    EventType = eventToPersist.EventType,
                    EventJson = eventToPersist.EventJson,
                    Metadata = metadata,
                    Created = eventToPersist.Created
                };
            });
    }

    private static ActivityContext? ExtractTraceContextFromMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("traceparent", out var traceParent) || string.IsNullOrEmpty(traceParent))
            return null;

        metadata.TryGetValue("tracestate", out var traceState);

        if (ActivityContext.TryParse(traceParent, traceState, out var context))
            return context;

        return null;
    }

    private static IEventEnvelope MapToEventWithMeta(EventRecord record)
    {
        var metadata = string.IsNullOrEmpty(record.metadata)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(record.metadata)
            ?? new Dictionary<string, string>();

        metadata["_position"] = record.position.ToString();

        // Extract trace context for potential restoration
        var traceContext = ExtractTraceContextFromMetadata(metadata);
        if (traceContext.HasValue)
            metadata["_trace_context"] = "available"; // Flag that trace context is available

        return new EventEnvelope
        {
            Id = record.id,
            EventType = new EventType(record.event_type),
            EventJson = record.event_data,
            Metadata = metadata,
            Created = record.created_at
        };
    }

    // ReSharper disable InconsistentNaming
    private class EventRecord
    {
        public long position { get; set; }
        public Guid id { get; set; }
        public int tenant_id { get; set; }
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
    private static readonly AsyncLocal<TransactionContext?> _current = new AsyncLocal<TransactionContext?>();

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