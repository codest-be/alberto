using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL implementation of processor lease management using database-backed leases.
/// Leases expire and must be periodically renewed, enabling single-leader processing
/// per processor and automatic recovery when replicas crash.
/// </summary>
public sealed class PostgresProcessorLeaseManager : IProcessorLeaseManager
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SchemaQualifier _schema;

    public PostgresProcessorLeaseManager(
        NpgsqlDataSource dataSource,
        string? schema = null,
        TimeSpan? leaseDuration = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _schema = new SchemaQualifier(schema);
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc/>
    public TimeSpan LeaseDuration { get; }

    /// <inheritdoc/>
    public async Task<IProcessorLease?> TryAcquireAsync(
        string consumerId, string processorId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Expiry is computed by the database, not the caller: the same clock that writes
        // expires_at is the clock the WHERE below evaluates it against. Computing it from
        // DateTimeOffset.UtcNow would make the effective duration
        // LeaseDuration ± (replica clock − database clock) — a replica running behind writes
        // an expiry another replica can immediately claim while the first still believes it
        // holds the lease, which is exactly the double-holder that fencing exists to prevent.
        // now() is transaction-start time, so it is consistent between the SET and the WHERE.
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {_schema.Table("alberto_processor_leases")} (consumer_id, processor_id, replica_id, acquired_at, expires_at)
            VALUES (@consumer_id, @processor_id, @replica_id, now(), now() + @lease_duration)
            ON CONFLICT (consumer_id, processor_id) DO UPDATE
            SET replica_id = @replica_id,
                acquired_at = CASE
                    WHEN {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id THEN {_schema.Table("alberto_processor_leases")}.acquired_at
                    ELSE now()
                END,
                expires_at = now() + @lease_duration
            WHERE {_schema.Table("alberto_processor_leases")}.expires_at < now()
               OR {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id
            RETURNING expires_at", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var actualExpiresAt = reader.GetDateTime(0);
            return new ProcessorLease(processorId, new DateTimeOffset(actualExpiresAt, TimeSpan.Zero));
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            UPDATE {_schema.Table("alberto_processor_leases")}
            SET expires_at = now() + @lease_duration
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
            RETURNING processor_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        var renewed = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            renewed.Add(reader.GetString(0));
        }

        return renewed;
    }

    /// <inheritdoc/>
    public async Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_processor_leases")}
            WHERE consumer_id = @consumer_id AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProcessorLeaseInfo>> GetAllLeasesAsync(
        string consumerId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($@"
            SELECT processor_id, replica_id, acquired_at, expires_at
            FROM {_schema.Table("alberto_processor_leases")}
            WHERE consumer_id = @consumer_id
            ORDER BY processor_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);

        var leases = new List<ProcessorLeaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            leases.Add(new ProcessorLeaseInfo(
                reader.GetString(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }

        return leases;
    }
}
