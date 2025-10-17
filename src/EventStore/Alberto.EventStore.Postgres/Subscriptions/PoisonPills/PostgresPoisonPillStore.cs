using Alberto.EventStore.Subscriptions.PoisonPills;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.EventStore.Postgres.Subscriptions.PoisonPills;

public sealed class PostgresPoisonPillStore(
    string connectionString,
    string schema,
    ILogger<PostgresPoisonPillStore> logger)
    : IPoisonPillStore
{
    public async ValueTask StorePoisonPill(PoisonPill poisonPill, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            INSERT INTO {schema}.subscription_poison_pills
            (id, subscription_id, global_position, event_id, event_type, event_data, metadata,
             error_message, stack_trace, retry_count, first_failed_at, last_failed_at)
            VALUES
            (@Id, @SubscriptionId, @GlobalPosition, @EventId, @EventType, @EventData::jsonb, @Metadata::jsonb,
             @ErrorMessage, @StackTrace, @RetryCount, @FirstFailedAt, @LastFailedAt)
            ON CONFLICT (subscription_id, global_position)
            DO UPDATE SET
                retry_count = EXCLUDED.retry_count,
                last_failed_at = EXCLUDED.last_failed_at,
                error_message = EXCLUDED.error_message,
                stack_trace = EXCLUDED.stack_trace";

        await connection.ExecuteAsync(sql, poisonPill);

        logger.LogError(
            "Stored poison pill for subscription '{SubscriptionId}' at position {Position} after {RetryCount} retries",
            poisonPill.SubscriptionId,
            poisonPill.GlobalPosition,
            poisonPill.RetryCount
        );
    }

    public async ValueTask<PoisonPill?> GetPoisonPill(
        string subscriptionId,
        long position,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            SELECT id, subscription_id, global_position, event_id, event_type, event_data, metadata,
                   error_message, stack_trace, retry_count, first_failed_at, last_failed_at,
                   resolved_at, resolved_by, resolution_action
            FROM {schema}.subscription_poison_pills
            WHERE subscription_id = @SubscriptionId AND global_position = @Position";

        var result = await connection.QuerySingleOrDefaultAsync<PoisonPillRecord>(
            sql,
            new { SubscriptionId = subscriptionId, Position = position }
        );

        if (result == null)
            return null;

        return new PoisonPill(
            result.id,
            result.subscription_id,
            result.global_position,
            result.event_id,
            result.event_type,
            result.event_data,
            result.metadata,
            result.error_message,
            result.stack_trace,
            result.retry_count,
            result.first_failed_at,
            result.last_failed_at,
            result.resolved_at,
            result.resolved_by,
            result.resolution_action
        );
    }

    public async ValueTask ResolvePoisonPill(
        Guid poisonPillId,
        string resolvedBy,
        string action,
        CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            UPDATE {schema}.subscription_poison_pills
            SET resolved_at = @ResolvedAt,
                resolved_by = @ResolvedBy,
                resolution_action = @Action
            WHERE id = @Id";

        await connection.ExecuteAsync(sql,
            new { Id = poisonPillId, ResolvedAt = DateTimeOffset.UtcNow, ResolvedBy = resolvedBy, Action = action });

        logger.LogInformation(
            "Resolved poison pill {PoisonPillId} with action '{Action}' by '{ResolvedBy}'",
            poisonPillId,
            action,
            resolvedBy
        );
    }

    public async ValueTask<IReadOnlyList<PoisonPill>> GetAllPoisonPills(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var sql = $@"
            SELECT id, subscription_id, global_position, event_id, event_type, event_data, metadata,
                   error_message, stack_trace, retry_count, first_failed_at, last_failed_at,
                   resolved_at, resolved_by, resolution_action
            FROM {schema}.subscription_poison_pills";

        var results = await connection.QueryAsync<PoisonPillRecord>(sql);

        var poisonPills = results.Select(result => new PoisonPill(
            result.id,
            result.subscription_id,
            result.global_position,
            result.event_id,
            result.event_type,
            result.event_data,
            result.metadata,
            result.error_message,
            result.stack_trace,
            result.retry_count,
            result.first_failed_at,
            result.last_failed_at,
            result.resolved_at,
            result.resolved_by,
            result.resolution_action
        )).ToList();

        logger.LogDebug("Retrieved {Count} poison pills from database", poisonPills.Count);

        return poisonPills;
    }

    // ReSharper disable InconsistentNaming
    private record PoisonPillRecord
    {
        public Guid id { get; init; }
        public required string subscription_id { get; init; }
        public long global_position { get; init; }
        public Guid event_id { get; init; }
        public required string event_type { get; init; }
        public required string event_data { get; init; }
        public required string metadata { get; init; }
        public required string error_message { get; init; }
        public string? stack_trace { get; init; }
        public int retry_count { get; init; }
        public DateTimeOffset first_failed_at { get; init; }
        public DateTimeOffset last_failed_at { get; init; }
        public DateTimeOffset? resolved_at { get; init; }
        public string? resolved_by { get; init; }
        public string? resolution_action { get; init; }
    }
    // ReSharper restore InconsistentNaming
}