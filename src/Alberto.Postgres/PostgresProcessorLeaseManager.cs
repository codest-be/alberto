using Alberto.Leases;
using Npgsql;

namespace Alberto.Postgres;

/// <summary>
/// PostgreSQL implementation of processor lease management using database-backed leases.
/// Leases expire and must be periodically renewed, enabling single-leader processing
/// per processor and automatic recovery when replicas crash.
/// </summary>
internal sealed class PostgresProcessorLeaseManager : IProcessorLeaseManager, IRebalancingLeaseManager, IBatchProcessorLeaseManager
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
        string consumerId, string processorId, string replicaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Expiry is computed by the database, not the caller: the same clock that writes
        // expires_at is the clock the WHERE below evaluates it against. Computing it from
        // DateTimeOffset.UtcNow would make the effective duration
        // LeaseDuration ± (replica clock − database clock) — a replica running behind writes
        // an expiry another replica can immediately claim while the first still believes it
        // holds the lease, which is exactly the double-holder that fencing exists to prevent.
        // now() is transaction-start time, so it is consistent between the SET and the WHERE.
        // Every acquisition draws a fresh fence token, which the holder then presents on each
        // fenced checkpoint write. EXCLUDED.fence_token on the conflict arm reuses the one the
        // INSERT already generated, so the sequence is advanced once per statement. Renewal goes
        // through RenewLeasesAsync and deliberately leaves the token alone: it is the same
        // ownership continuing, and changing it would fence the holder out of its own checkpoint.
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO {_schema.Table("alberto_processor_leases")} (consumer_id, processor_id, replica_id, acquired_at, expires_at, fence_token)
            VALUES (@consumer_id, @processor_id, @replica_id, now(), now() + @lease_duration, nextval('{_schema.Table("alberto_fence_tokens")}'))
            ON CONFLICT (consumer_id, processor_id) DO UPDATE
            SET replica_id = @replica_id,
                acquired_at = CASE
                    WHEN {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id THEN {_schema.Table("alberto_processor_leases")}.acquired_at
                    ELSE now()
                END,
                expires_at = now() + @lease_duration,
                fence_token = EXCLUDED.fence_token
            WHERE {_schema.Table("alberto_processor_leases")}.expires_at < now()
               OR {_schema.Table("alberto_processor_leases")}.replica_id = @replica_id
            RETURNING expires_at, fence_token", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var actualExpiresAt = reader.GetDateTime(0);
            return new ProcessorLease(
                processorId,
                new DateTimeOffset(actualExpiresAt, TimeSpan.Zero),
                reader.GetInt64(1));
        }

        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The rules of <see cref="TryAcquireAsync"/>, set-based. Unlike that upsert, which draws its
    /// token in VALUES before it knows whether it wins, nextval runs only on rows actually taken:
    /// existing rows go through an UPDATE over the claimable ones, absent ids through an INSERT
    /// that does nothing on conflict. So contending for leases held by a live replica draws no
    /// token; only an INSERT that loses a race for a brand-new row still does, and a gap in the
    /// sequence is harmless. SKIP LOCKED leaves a row another acquirer is taking right now to
    /// that acquirer; the next scan sees the outcome.
    /// </remarks>
    public async Task<IReadOnlyList<IProcessorLease>> TryAcquireManyAsync(
        string consumerId, IReadOnlyCollection<string> processorIds, string replicaId,
        CancellationToken cancellationToken)
    {
        var leases = _schema.Table("alberto_processor_leases");
        var nextToken = $"nextval('{_schema.Table("alberto_fence_tokens")}')";

        await using var cmd = _dataSource.CreateCommand($@"
            WITH claimable AS (
                SELECT processor_id FROM {leases}
                WHERE consumer_id = @consumer_id
                  AND processor_id = ANY(@processor_ids)
                  AND (expires_at < now() OR replica_id = @replica_id)
                ORDER BY processor_id
                FOR UPDATE SKIP LOCKED
            ),
            taken AS (
                UPDATE {leases} l
                SET replica_id = @replica_id,
                    acquired_at = CASE WHEN l.replica_id = @replica_id THEN l.acquired_at ELSE now() END,
                    expires_at = now() + @lease_duration,
                    fence_token = {nextToken}
                FROM claimable x
                WHERE l.consumer_id = @consumer_id AND l.processor_id = x.processor_id
                  AND (l.expires_at < now() OR l.replica_id = @replica_id)
                RETURNING l.processor_id, l.expires_at, l.fence_token
            ),
            inserted AS (
                INSERT INTO {leases} (consumer_id, processor_id, replica_id, acquired_at, expires_at, fence_token)
                SELECT @consumer_id, w.processor_id, @replica_id, now(), now() + @lease_duration, {nextToken}
                FROM (SELECT DISTINCT unnest(@processor_ids) AS processor_id) w
                WHERE NOT EXISTS (
                    SELECT 1 FROM {leases} l
                    WHERE l.consumer_id = @consumer_id AND l.processor_id = w.processor_id)
                ORDER BY w.processor_id
                ON CONFLICT (consumer_id, processor_id) DO NOTHING
                RETURNING processor_id, expires_at, fence_token
            )
            SELECT * FROM taken
            UNION ALL
            SELECT * FROM inserted");

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("processor_ids", processorIds.ToArray());
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        var won = new List<IProcessorLease>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            won.Add(new ProcessorLease(
                reader.GetString(0),
                new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                reader.GetInt64(2)));

        return won;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

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
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            renewed.Add(reader.GetString(0));
        }

        return renewed;
    }

    /// <inheritdoc/>
    public async Task<int> CountLiveReplicasAsync(string consumerId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            SELECT count(DISTINCT replica_id)::int FROM {_schema.Table("alberto_processor_leases")}
            WHERE consumer_id = @consumer_id AND expires_at > now()", connection);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RenewLeasesAsync(
        string consumerId, string replicaId, IReadOnlyCollection<string> processorIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            UPDATE {_schema.Table("alberto_processor_leases")}
            SET expires_at = now() + @lease_duration
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
              AND processor_id = ANY(@processor_ids)
            RETURNING processor_id", connection);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("processor_ids", processorIds.ToArray());
        cmd.Parameters.AddWithValue("lease_duration", LeaseDuration);

        var renewed = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            renewed.Add(reader.GetString(0));
        return renewed;
    }

    /// <inheritdoc/>
    public async Task ReleaseLeasesAsync(
        string consumerId, string replicaId, IReadOnlyCollection<string> processorIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_processor_leases")}
            WHERE consumer_id = @consumer_id
              AND replica_id = @replica_id
              AND processor_id = ANY(@processor_ids)", connection);
        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);
        cmd.Parameters.AddWithValue("processor_ids", processorIds.ToArray());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ReleaseAllLeasesAsync(
        string consumerId, string replicaId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($@"
            DELETE FROM {_schema.Table("alberto_processor_leases")}
            WHERE consumer_id = @consumer_id AND replica_id = @replica_id", connection);

        cmd.Parameters.AddWithValue("consumer_id", consumerId);
        cmd.Parameters.AddWithValue("replica_id", replicaId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
