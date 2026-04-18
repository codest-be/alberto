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
        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {_schema.Table("alberto_processor_leases")} (consumer_id, processor_id, replica_id, acquired_at, expires_at)
            VALUES (@consumer_id, @processor_id, @replica_id, NOW(), @expires_at)
            ON CONFLICT (consumer_id, processor_id) DO UPDATE
            SET replica_id = @replica_id,
                acquired_at = CASE
                    WHEN {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id THEN {_schema.Table("alberto_processor_leases")}.acquired_at
                    ELSE NOW()
                END,
                expires_at = @expires_at
            WHERE {_schema.Table("alberto_processor_leases")}.expires_at < NOW()
               OR {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id
            RETURNING expires_at", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("expires_at", expiresAt);

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
        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseDuration);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            UPDATE {_schema.Table("alberto_processor_leases")}
            SET expires_at = @expires_at
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
            RETURNING processor_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("expires_at", expiresAt);

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
