using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of tenant processor lock using database-backed leases.
/// Leases expire and must be periodically renewed, enabling fair distribution
/// and automatic recovery when replicas crash.
/// </summary>
public sealed class PostgresTenantProcessorLock : ITenantProcessorLock
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    /// <summary>
    /// Creates a new PostgresTenantProcessorLock.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name (e.g., "orders"). Can be null for default schema.</param>
    /// <param name="leaseDuration">How long a lease is valid before expiring. Default is 60 seconds.</param>
    public PostgresTenantProcessorLock(
        NpgsqlDataSource dataSource,
        string? schema = null,
        TimeSpan? leaseDuration = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(60);
    }

    /// <inheritdoc/>
    public TimeSpan LeaseDuration { get; }

    /// <inheritdoc/>
    public async Task<ITenantLease?> TryAcquireForTenantAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Try to insert a new lease, or update an existing one if it has expired.
        // The UPDATE only succeeds if expires_at < NOW() (expired) OR replica_id matches (own lease).
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {_schema.Table("tenant_leases")} (consumer_id, tenant_id, replica_id, acquired_at, expires_at)
            VALUES (@consumer_id, @tenant_id, @replica_id, NOW(), @expires_at)
            ON CONFLICT (consumer_id, tenant_id) DO UPDATE
            SET replica_id = @replica_id,
                acquired_at = CASE
                    WHEN {_schema.Table("tenant_leases")}.replica_id = @replica_id THEN {_schema.Table("tenant_leases")}.acquired_at
                    ELSE NOW()
                END,
                expires_at = @expires_at
            WHERE {_schema.Table("tenant_leases")}.expires_at < NOW()
               OR {_schema.Table("tenant_leases")}.replica_id = @replica_id
            RETURNING expires_at", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("expires_at", expiresAt);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var actualExpiresAt = reader.GetDateTime(0);
            return new TenantLease(tenantId, new DateTimeOffset(actualExpiresAt, TimeSpan.Zero));
        }

        // No rows returned means the lease is held by another replica and not expired
        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Update expires_at for all leases owned by this replica
        await using var cmd = new NpgsqlCommand($@"
            UPDATE {_schema.Table("tenant_leases")}
            SET expires_at = @expires_at
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
            RETURNING tenant_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("expires_at", expiresAt);

        var renewedTenants = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            renewedTenants.Add(reader.GetString(0));
        }

        return renewedTenants;
    }

    /// <inheritdoc/>
    public async Task ReleaseLeaseAsync(
        string consumerId, string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("tenant_leases")}
            WHERE consumer_id = @consumer_id AND tenant_id = @tenant_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ReleaseTenantLeaseAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("tenant_leases")}
            WHERE consumer_id = @consumer_id
              AND tenant_id = @tenant_id
              AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("tenant_leases")}
            WHERE consumer_id = @consumer_id AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetKnownTenantsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT DISTINCT tenant_id FROM {_schema.Table("events")} ORDER BY tenant_id",
            connection);

        var tenants = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(reader.GetString(0));
        }

        return tenants;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantLeaseInfo>> GetAllLeasesAsync(
        string consumerId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($@"
            SELECT tenant_id, replica_id, acquired_at, expires_at
            FROM {_schema.Table("tenant_leases")}
            WHERE consumer_id = @consumer_id
            ORDER BY tenant_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);

        var leases = new List<TenantLeaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            leases.Add(new TenantLeaseInfo(
                reader.GetString(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }

        return leases;
    }
}
