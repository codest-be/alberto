using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// Single-tenant PostgreSQL implementation of the event store backend.
/// Uses PostgreSQL functions with inverted indexes for efficient DCB queries.
/// No tenant scoping — one event store per database/schema.
/// For multi-tenant mode, use .WithTenancy() which wraps this with TenantEventStoreDecorator.
/// </summary>
/// <remarks>
/// All non-trivial logic (stream query tree, append path, positions, stable-head
/// barrier) lives in <see cref="PostgresBackendHelpers"/> as static methods
/// parameterised on tenancy. This class is a thin adapter that supplies the
/// constructor context (data source, schema, lock key) and delegates.
/// </remarks>
public sealed class PostgresEventStoreBackend(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null,
    string? schema = null,
    bool enableStableHeadBarrier = true)
    : IEventStoreBackend, IEventStoreHeadBackend
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    // _timeProvider is retained for future use and to preserve the constructor surface.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SchemaQualifier _schema = new(schema);
    private readonly bool _enableStableHeadBarrier = enableStableHeadBarrier;

    // Single-tenant: one append lock per store (schema). Serializes all appends
    // for this store so the DCB conflict-check and insert are atomic — see AppendCoreAsync.
    private readonly string _appendLockKey = $"alberto-append:{schema ?? ""}";

    public async Task<IReadOnlyCollection<IEventEnvelope>> ReadAsync(
        DcbQuery query,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = PostgresBackendHelpers.BuildStreamQuery(_schema, query, tenanted: false);
        await using var cmd = new NpgsqlCommand(sql, connection);
        PostgresBackendHelpers.AddStreamParameters(cmd, query, tenantId: null, afterPosition, limit);

        return await PostgresBackendHelpers.ReadEventsAsync(cmd, includeTenantId: false, tenantId: null, cancellationToken);
    }

    public IAsyncEnumerable<IEventEnvelope> StreamAsync(
        DcbQuery query,
        long afterPosition = 0,
        CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.StreamEventsAsync(
            _dataSource,
            PostgresBackendHelpers.BuildStreamQuery(_schema, query, tenanted: false),
            cmd => PostgresBackendHelpers.AddStreamParameters(cmd, query, tenantId: null, afterPosition, limit: null),
            includeTenantId: false,
            cancellationToken);

    public async Task<IReadOnlyCollection<IEventEnvelope>> ReadAllAsync(
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ReadAllSql, connection);
        BindReadAll(cmd, afterPosition, limit);

        return await PostgresBackendHelpers.ReadEventsAsync(cmd, includeTenantId: false, tenantId: null, cancellationToken);
    }

    public IAsyncEnumerable<IEventEnvelope> StreamAllAsync(
        long afterPosition = 0,
        CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.StreamEventsAsync(
            _dataSource, ReadAllSql, cmd => BindReadAll(cmd, afterPosition, limit: null),
            includeTenantId: false, cancellationToken);

    private string ReadAllSql =>
        $"SELECT * FROM {_schema.Function("alberto_read_all")}(@p_after_position, @p_limit)";

    private static void BindReadAll(NpgsqlCommand cmd, long afterPosition, int? limit)
    {
        cmd.Parameters.AddWithValue("p_after_position", afterPosition);
        cmd.Parameters.AddWithValue("p_limit", limit.HasValue ? limit.Value : DBNull.Value);
    }

    public Task<IReadOnlyCollection<IEventEnvelope>> ReadAllOfTypesAsync(
        IReadOnlySet<string> eventTypes,
        long afterPosition,
        int limit,
        CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.ReadAllOfTypesAsync(
            _schema, _dataSource, tenanted: false, eventTypes, afterPosition, limit, cancellationToken);

    public async Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
        IEnumerable<IEventToPersist> events,
        DcbQuery? dcbQuery = null,
        long? expectedPosition = null,
        CancellationToken cancellationToken = default)
    {
        var eventsList = events.ToList();
        if (eventsList.Count == 0)
            return [];

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await PostgresBackendHelpers.AppendCoreAsync(
            connection, _schema, _appendLockKey,
            tenantId: null, eventsList, dcbQuery, expectedPosition, cancellationToken);
    }

    public async Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {_schema.Function("alberto_get_last_position")}()",
            connection);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long position ? position : 0;
    }

    public Task<IReadOnlyList<long>> GetPositionsAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.GetPositionsAsync(_schema, _dataSource, afterPosition, windowSize, cancellationToken);

    public Task<long> GetStableHeadAsync(
        long afterPosition, CancellationToken cancellationToken = default)
        => GetStableHeadUpToAsync(afterPosition, long.MaxValue, cancellationToken);

    public Task<long> GetStableHeadUpToAsync(
        long afterPosition, long upToPosition, CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.GetStableHeadAsync(_schema, _dataSource, _enableStableHeadBarrier, afterPosition, upToPosition, cancellationToken);
}
