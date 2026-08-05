using System.Diagnostics;
using Alberto.Subscriptions;
using Alberto.Telemetry;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of tenant processor lock using database-backed leases.
/// Leases expire and must be periodically renewed, enabling fair distribution
/// and automatic recovery when replicas crash.
/// </summary>
public sealed class PostgresTenantProcessorLock : ITenantProcessorLock
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;
    private readonly string? _moduleKey;

    // In-memory tracking of which tenants each consumerId currently owns in this process.
    // Updated synchronously with the DB operation so the observable gauge always reflects
    // the last known ownership state. A consumer that releases all tenants gets a zero
    // snapshot (not absent) — a stale non-zero gauge is worse than a visible zero.
    private readonly Dictionary<string, HashSet<string>> _ownedByConsumer = new(StringComparer.Ordinal);
    private readonly object _ownedLock = new();

    /// <summary>
    /// Creates a new PostgresTenantProcessorLock.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="schema">The database schema name (e.g., "orders"). Can be null for default schema.</param>
    /// <param name="leaseDuration">How long a lease is valid before expiring. Default is 60 seconds.</param>
    /// <param name="moduleKey">
    /// The module key used to tag <c>alberto.owned_tenant_count</c> gauge measurements.
    /// When null the gauge is not updated — production registrations always supply this via
    /// <see cref="PostgresBackendDescriptor"/>.
    /// </param>
    public PostgresTenantProcessorLock(
        NpgsqlDataSource dataSource,
        string? schema = null,
        TimeSpan? leaseDuration = null,
        string? moduleKey = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(60);
        _moduleKey = moduleKey;
    }

    /// <inheritdoc/>
    public TimeSpan LeaseDuration { get; }

    // Upserts the owned-tenant snapshot for consumerId and emits the gauge.
    // No-op when _moduleKey is null (tests that don't care about the gauge
    // can skip passing moduleKey to keep their construction site simple).
    private void RecordOwnership(string consumerId)
    {
        if (_moduleKey is null) return;
        int count;
        lock (_ownedLock)
        {
            count = _ownedByConsumer.TryGetValue(consumerId, out var set) ? set.Count : 0;
        }
        AlbertoMetrics.RecordTenantOwnership(consumerId, _moduleKey, count);
    }

    /// <inheritdoc/>
    public async Task<ITenantLease?> TryAcquireForTenantAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Try to insert a new lease, or update an existing one if it has expired.
        // The UPDATE only succeeds if expires_at < now() (expired) OR replica_id matches (own lease).
        //
        // Expiry is computed by the database, not the caller, so that the same clock which
        // writes expires_at is the clock the WHERE evaluates it against. Computing it from
        // DateTimeOffset.UtcNow would make the effective duration
        // LeaseDuration ± (replica clock − database clock). now() is transaction-start time,
        // so it is consistent between the SET and the WHERE.
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {_schema.Table("alberto_tenant_leases")} (consumer_id, tenant_id, replica_id, acquired_at, expires_at)
            VALUES (@consumer_id, @tenant_id, @replica_id, now(), now() + @lease_duration)
            ON CONFLICT (consumer_id, tenant_id) DO UPDATE
            SET replica_id = @replica_id,
                acquired_at = CASE
                    WHEN {_schema.Table("alberto_tenant_leases")}.replica_id = @replica_id THEN {_schema.Table("alberto_tenant_leases")}.acquired_at
                    ELSE now()
                END,
                expires_at = now() + @lease_duration
            WHERE {_schema.Table("alberto_tenant_leases")}.expires_at < now()
               OR {_schema.Table("alberto_tenant_leases")}.replica_id = @replica_id
            RETURNING expires_at", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Deliberately tagged by consumer.id ONLY. A tenant id is unbounded and grows with the
        // customer base, and every distinct tag combination is a separate time series the SDK
        // allocates for the life of the process and exports on every collection cycle — so
        // tagging by tenant makes these two counters most expensive exactly when the product is
        // doing best, and takes the whole metrics pipeline down rather than degrading. The
        // per-tenant question ("did tenant X get its lease?") is a trace/log question about one
        // event; the aggregate question ("is this consumer's failure rate rising?") is what a
        // counter is for, and consumer.id answers it. For tenant fanout without the cardinality,
        // see the alberto.owned_tenant_count gauge.
        if (await reader.ReadAsync(ct))
        {
            AlbertoMetrics.TenantLocksAcquired.Add(1, new TagList { { "consumer.id", consumerId } });
            var actualExpiresAt = reader.GetDateTime(0);

            lock (_ownedLock)
            {
                if (!_ownedByConsumer.TryGetValue(consumerId, out var set))
                    _ownedByConsumer[consumerId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(tenantId);
            }
            RecordOwnership(consumerId);

            return new TenantLease(tenantId, new DateTimeOffset(actualExpiresAt, TimeSpan.Zero));
        }

        // No rows returned means the lease is held by another replica and not expired
        AlbertoMetrics.TenantLockFailures.Add(1, new TagList { { "consumer.id", consumerId } });
        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Update expires_at for all leases owned by this replica
        await using var cmd = new NpgsqlCommand($@"
            UPDATE {_schema.Table("alberto_tenant_leases")}
            SET expires_at = now() + @lease_duration
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
            RETURNING tenant_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        var renewedTenants = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            renewedTenants.Add(reader.GetString(0));
        }

        // Replace the in-memory set with exactly what the database renewed.
        // Tenants that were not renewed (lease stolen or expired) fall off automatically.
        lock (_ownedLock)
        {
            _ownedByConsumer[consumerId] = new HashSet<string>(renewedTenants, StringComparer.Ordinal);
        }
        RecordOwnership(consumerId);

        return renewedTenants;
    }

    /// <inheritdoc/>
    public async Task ReleaseLeaseAsync(
        string consumerId, string tenantId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_tenant_leases")}
            WHERE consumer_id = @consumer_id AND tenant_id = @tenant_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);

        await cmd.ExecuteNonQueryAsync(ct);

        lock (_ownedLock)
        {
            if (_ownedByConsumer.TryGetValue(consumerId, out var set))
                set.Remove(tenantId);
        }
        RecordOwnership(consumerId);
    }

    /// <inheritdoc/>
    public async Task ReleaseTenantLeaseAsync(
        string consumerId, string tenantId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_tenant_leases")}
            WHERE consumer_id = @consumer_id
              AND tenant_id = @tenant_id
              AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("tenant_id", tenantId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(ct);

        lock (_ownedLock)
        {
            if (_ownedByConsumer.TryGetValue(consumerId, out var set))
                set.Remove(tenantId);
        }
        RecordOwnership(consumerId);
    }

    /// <inheritdoc/>
    public async Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_tenant_leases")}
            WHERE consumer_id = @consumer_id AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(ct);

        // Emit a zero snapshot rather than removing the entry: a stale non-zero gauge is
        // worse than a visible zero, and zero is the correct answer after a full release.
        lock (_ownedLock)
        {
            _ownedByConsumer[consumerId] = new HashSet<string>(StringComparer.Ordinal);
        }
        RecordOwnership(consumerId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the <c>alberto_tenants</c> catalog rather than scanning the event log. The
    /// catalog is what migration 012 created this method in mind for: a statement-level
    /// trigger on <c>alberto_events</c> upserts each tenant in the appended batch, and the
    /// migration backfills from the existing log, so an established store is complete from
    /// the moment it migrates. The trigger fires inside the appender's own transaction, so
    /// a new tenant becomes discoverable at the instant its first event is durable — the
    /// same moment a <c>SELECT DISTINCT</c> over the log would have found it.
    ///
    /// The two disagree in one case: the catalog has no delete path, so a tenant whose
    /// events have been purged keeps its row. Distribution then hands out a lease for a
    /// tenant with no work, which costs a lease row and an empty pass, rather than
    /// re-scanning the whole event log on every startup to avoid it.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetKnownTenantsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT tenant_id FROM {_schema.Table("alberto_tenants")} ORDER BY tenant_id",
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
            FROM {_schema.Table("alberto_tenant_leases")}
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
