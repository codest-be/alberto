using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// Multi-tenant PostgreSQL event store backend.
/// Exposes tenant-scoped methods used by <see cref="TenantEventStoreDecorator"/>.
/// Not registered directly as IEventStoreBackend; use .WithTenancy() to enable.
/// </summary>
/// <remarks>
/// All non-trivial logic (stream query tree, append path, positions, stable-head
/// barrier) lives in <see cref="PostgresBackendHelpers"/> as static methods
/// parameterised on tenancy. This class is a thin adapter that supplies the
/// constructor context and delegates, adding the tenant-id seam where needed.
/// </remarks>
internal sealed class PostgresTenantEventStoreBackend(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null,
    string? schema = null,
    bool enableStableHeadBarrier = true)
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    // _timeProvider is retained for future use and to preserve the constructor surface.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SchemaQualifier _schema = new(schema);
    private readonly bool _enableStableHeadBarrier = enableStableHeadBarrier;
    // Raw schema string used to build the per-tenant advisory-lock key.
    private readonly string? _schemaName = schema;

    public async Task<IReadOnlyCollection<IEventEnvelope>> StreamForTenant(
        string tenantId,
        DcbQuery query,
        long afterPosition = 0,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = PostgresBackendHelpers.BuildStreamQuery(_schema, query, tenanted: true);
        await using var cmd = new NpgsqlCommand(sql, connection);
        PostgresBackendHelpers.AddStreamParameters(cmd, query, tenantId, afterPosition, limit);

        // The multi-tenant stream result set includes a tenant_id column.
        return await PostgresBackendHelpers.ReadEventsAsync(cmd, includeTenantId: true, tenantId: null, cancellationToken);
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

        return await PostgresBackendHelpers.ReadEventsAsync(cmd, includeTenantId: true, tenantId: null, cancellationToken);
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

        // #1 Write-skew fix: per-tenant granularity is sufficient because DCB queries
        // are tenant-scoped — appends to different tenants can never conflict — so
        // cross-tenant append concurrency is preserved. Released on commit/rollback.
        var lockKey = $"alberto-append:{_schemaName ?? ""}:{tenantId}";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await PostgresBackendHelpers.AppendCoreAsync(
            connection, transaction: null, _schema, lockKey,
            tenantId, eventsList, dcbQuery, expectedPosition, cancellationToken);
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

    public Task<IReadOnlyList<long>> GetPositionsGlobalAsync(
        long afterPosition, int windowSize, CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.GetPositionsAsync(_schema, _dataSource, afterPosition, windowSize, cancellationToken);

    public Task<long> GetStableHeadGlobalAsync(
        long afterPosition, CancellationToken cancellationToken = default)
        => PostgresBackendHelpers.GetStableHeadAsync(_schema, _dataSource, _enableStableHeadBarrier, afterPosition, cancellationToken);
}
