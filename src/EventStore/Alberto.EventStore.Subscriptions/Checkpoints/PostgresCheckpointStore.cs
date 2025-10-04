using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.EventStore.Subscriptions.Checkpoints;

public sealed class PostgresCheckpointStore(
    string connectionString,
    string schema,
    ILogger<PostgresCheckpointStore> logger)
    : ICheckpointStore
{
    public async ValueTask<Checkpoint> GetLastCheckpoint(
        string subscriptionId,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            SELECT subscription_id, position, updated_at
            FROM {schema}.subscription_checkpoints
            WHERE subscription_id = @SubscriptionId";

        var result = await connection.QuerySingleOrDefaultAsync<CheckpointRecord>(
            sql,
            new { SubscriptionId = subscriptionId }
        );

        if (result != null)
        {
            return new Checkpoint(
                result.subscription_id,
                result.position,
                result.updated_at
            );
        }

        // Create new checkpoint at position NULL (start from beginning)
        var newCheckpoint = new Checkpoint(subscriptionId, null, DateTimeOffset.UtcNow);

        var insertSql = $@"
            INSERT INTO {schema}.subscription_checkpoints (subscription_id, position, updated_at)
            VALUES (@SubscriptionId, @Position, @UpdatedAt)
            ON CONFLICT (subscription_id) DO NOTHING";

        await connection.ExecuteAsync(insertSql,
            new { SubscriptionId = subscriptionId, Position = (long?)null, newCheckpoint.UpdatedAt });

        logger.LogInformation(
            "Created new checkpoint for subscription '{SubscriptionId}'",
            subscriptionId
        );

        return newCheckpoint;
    }

    public async ValueTask<Checkpoint> StoreCheckpoint(
        Checkpoint checkpoint,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            INSERT INTO {schema}.subscription_checkpoints (subscription_id, position, updated_at)
            VALUES (@SubscriptionId, @Position, @UpdatedAt)
            ON CONFLICT (subscription_id)
            DO UPDATE SET
                position = EXCLUDED.position,
                updated_at = EXCLUDED.updated_at";

        var updatedCheckpoint = checkpoint with { UpdatedAt = DateTimeOffset.UtcNow };

        await connection.ExecuteAsync(sql,
            new { updatedCheckpoint.SubscriptionId, updatedCheckpoint.Position, updatedCheckpoint.UpdatedAt });

        return updatedCheckpoint;
    }

    // ReSharper disable InconsistentNaming
    private record CheckpointRecord
    {
        public required string subscription_id { get; init; }
        public long? position { get; init; }
        public DateTimeOffset updated_at { get; init; }
    }
    // ReSharper restore InconsistentNaming
}