using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Resolves the migrated PostgreSQL store shape once for an adapter. The database schema is
/// authoritative; an optional expected mode is a compatibility assertion, not a caller-controlled
/// switch.
/// </summary>
internal sealed class PostgresStoreTopology
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;
    private readonly bool? _expectedMultiTenant;
    private int _resolvedMode = -1;

    public PostgresStoreTopology(
        NpgsqlDataSource dataSource,
        string? schema,
        bool? expectedMultiTenant = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        _expectedMultiTenant = expectedMultiTenant;
    }

    public async ValueTask<bool> IsMultiTenantAsync(CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _resolvedMode);
        if (cached >= 0)
            return cached == 1;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await IsMultiTenantAsync(connection, ct);
    }

    public async ValueTask<bool> IsMultiTenantAsync(
        NpgsqlConnection connection,
        CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _resolvedMode);
        if (cached >= 0)
            return cached == 1;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND table_name = 'alberto_events'
                  AND column_name = 'tenant_id'
            )
            """;
        command.Parameters.AddWithValue("schema", _schema.Name);

        var detected = await command.ExecuteScalarAsync(ct) is true;
        if (_expectedMultiTenant is { } expected && expected != detected)
        {
            var expectedName = expected ? "multi-tenant" : "single-tenant";
            var detectedName = detected ? "multi-tenant" : "single-tenant";
            throw new InvalidOperationException(
                $"Alberto tenancy mismatch (schema '{_schema.Name}'): the adapter expected " +
                $"{expectedName} storage, but the migrated schema is {detectedName}.");
        }

        var resolved = detected ? 1 : 0;
        var winner = Interlocked.CompareExchange(ref _resolvedMode, resolved, -1);
        return (winner >= 0 ? winner : resolved) == 1;
    }
}
