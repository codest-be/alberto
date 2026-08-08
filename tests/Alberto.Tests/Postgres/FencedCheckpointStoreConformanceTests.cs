using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Runs the <see cref="FencedCheckpointStoreSpecification"/> against the PostgreSQL adapter pair:
/// <see cref="PostgresProcessorLeaseManager"/> + <see cref="PostgresCheckpointStore"/>.
///
/// Uses <see cref="SingleTenantPostgresFixture"/> because <c>alberto_processor_leases</c> is
/// created only by the single-tenant migration set. All fencing SQL (the stored procedures and
/// the lease table) lives in that migration path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresFencedCheckpointStoreConformanceTests(SingleTenantPostgresFixture fixture)
    : FencedCheckpointStoreSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    protected override Task<IProcessorLeaseManager> CreateLeaseManager() =>
        Task.FromResult<IProcessorLeaseManager>(
            new PostgresProcessorLeaseManager(fixture.DataSource));

    protected override Task<IFencedCheckpointStore> CreateStore() =>
        Task.FromResult<IFencedCheckpointStore>(
            new PostgresCheckpointStore(fixture.DataSource));

    /// <summary>
    /// Expires the lease for <paramref name="processorId"/> in the database by setting
    /// <c>expires_at</c> to an hour in the past. This makes the lease appear expired on
    /// the next database-side evaluation (the fencing stored procedure checks
    /// <c>expires_at > now()</c> inside the transaction).
    /// </summary>
    protected override async Task ExpireLeaseAsync(string processorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE alberto_processor_leases
            SET expires_at = now() - INTERVAL '1 hour'
            WHERE consumer_id = @consumer_id AND processor_id = @processor_id
            """,
            connection);
        cmd.Parameters.AddWithValue("consumer_id", ConsumerId);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds the checkpoint row in the database with the supplied position and fence token,
    /// bypassing all guards. Used by the specification to exercise guard 2 (checkpoint-row
    /// fence-token check) in isolation.
    /// </summary>
    protected override async Task SeedCheckpointFenceTokenAsync(
        string processorId, long position, long fenceToken)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_processor_checkpoints (processor_id, last_position, fence_token)
            VALUES (@processor_id, @position, @fence_token)
            ON CONFLICT (processor_id) DO UPDATE
                SET last_position = @position, fence_token = @fence_token
            """,
            connection);
        cmd.Parameters.AddWithValue("processor_id", processorId);
        cmd.Parameters.AddWithValue("position", position);
        cmd.Parameters.AddWithValue("fence_token", fenceToken);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
