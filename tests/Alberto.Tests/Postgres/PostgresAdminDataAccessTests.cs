using Alberto.Admin;
using Alberto.Postgres;
using Alberto.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// A private database on the shared cluster running the shipped single-tenant migrations.
/// No manual schema patching is applied — the tests exercise the schema exactly as it is
/// delivered to production.
/// </summary>
public sealed class PostgresAdminDataAccessFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.SingleTenant);

/// <summary>
/// Integration tests for <see cref="PostgresAdminDataAccess"/>, focusing on the composite
/// transactional mutation <see cref="PostgresAdminDataAccess.RetryByRewindAsync"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresAdminDataAccessTests(PostgresAdminDataAccessFixture fixture)
    : IClassFixture<PostgresAdminDataAccessFixture>
{
    private PostgresAdminDataAccess CreateAdmin() => new(fixture.DataSource);

    private PostgresCheckpointStore CreateCheckpointStore() => new(fixture.DataSource);

    private async Task InsertDeadLetterAsync(string processorId, long globalPosition, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_dead_letter_events
                (id, processor_id, event_id, event_type, event_data, global_position,
                 error_message, attempt_count, failed_at)
            VALUES
                (@id, @processorId, @eventId, 'test-event', '{}'::jsonb, @globalPosition,
                 'boom', 4, now())
            """,
            conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("processorId", processorId);
        cmd.Parameters.AddWithValue("eventId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("globalPosition", globalPosition);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task RetryByRewindAsync_RewindsToOneBeforeEarliestDeadLetter_AndClearsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        await checkpoints.SaveAsync(processorId, 100, ct);
        await InsertDeadLetterAsync(processorId, 42, ct);
        await InsertDeadLetterAsync(processorId, 57, ct);

        var (rewindPosition, deletedCount) = await CreateAdmin().RetryByRewindAsync(processorId, ct);

        Assert.Equal(41, rewindPosition);
        Assert.Equal(2, deletedCount);
        Assert.Equal(41, await checkpoints.GetAsync(processorId, ct));
    }

    [Fact]
    public async Task RetryByRewindAsync_NoDeadLetters_LeavesCheckpointUntouched()
    {
        // Without the guard inside the method, MIN(global_position) over an empty set is
        // NULL, which coerced to 0 and rewound the checkpoint to -1 — replaying the
        // processor's entire history. The invariant belongs to the module, not the caller.
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        await checkpoints.SaveAsync(processorId, 100, ct);

        var (rewindPosition, deletedCount) = await CreateAdmin().RetryByRewindAsync(processorId, ct);

        Assert.Null(rewindPosition);
        Assert.Equal(0, deletedCount);
        Assert.Equal(100, await checkpoints.GetAsync(processorId, ct));
    }

    [Fact]
    public async Task RetryByRewindAsync_NoCheckpointRow_CreatesOneAtRewindPosition()
    {
        // A processor can have dead letters recorded before any checkpoint row exists.
        // The rewind must still take effect, so the write is an upsert rather than an update.
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"proc-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        await InsertDeadLetterAsync(processorId, 7, ct);
        Assert.Null(await checkpoints.GetAsync(processorId, ct));

        var (rewindPosition, deletedCount) = await CreateAdmin().RetryByRewindAsync(processorId, ct);

        Assert.Equal(6, rewindPosition);
        Assert.Equal(1, deletedCount);
        Assert.Equal(6, await checkpoints.GetAsync(processorId, ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_MovesPositionAndRemovesSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"from-{Guid.NewGuid():N}";
        var to = $"to-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(from, 123, ct);

        var result = await CreateAdmin().RenameCheckpointAsync(from, to, ct);

        Assert.Equal(CheckpointRenameStatus.Renamed, result.Status);
        Assert.Equal(123, result.Position);
        Assert.Null(await checkpoints.GetAsync(from, ct));
        Assert.Equal(123, await checkpoints.GetAsync(to, ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_MissingSource_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"missing-{Guid.NewGuid():N}";
        var to = $"to-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();

        var result = await CreateAdmin().RenameCheckpointAsync(from, to, ct);

        Assert.Equal(CheckpointRenameStatus.SourceNotFound, result.Status);
        Assert.Null(result.Position);
        Assert.Null(await checkpoints.GetAsync(from, ct));
        Assert.Null(await checkpoints.GetAsync(to, ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_ExistingDestination_DoesNotOverwriteOrDeleteSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var from = $"from-{Guid.NewGuid():N}";
        var to = $"to-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(from, 123, ct);
        await checkpoints.SaveAsync(to, 999, ct);

        var result = await CreateAdmin().RenameCheckpointAsync(from, to, ct);

        Assert.Equal(CheckpointRenameStatus.DestinationExists, result.Status);
        Assert.Equal(999, result.Position);
        Assert.Equal(123, await checkpoints.GetAsync(from, ct));
        Assert.Equal(999, await checkpoints.GetAsync(to, ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_SameId_IsRejectedWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"same-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(processorId, 77, ct);

        var result = await CreateAdmin().RenameCheckpointAsync(processorId, processorId, ct);

        Assert.Equal(CheckpointRenameStatus.SameProcessorId, result.Status);
        Assert.Equal(77, await checkpoints.GetAsync(processorId, ct));
    }

    [Fact]
    public async Task RenameCheckpointAsync_ConcurrentDestinationRace_HasOneWinnerAndNoPartialMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceA = $"source-a-{Guid.NewGuid():N}";
        var sourceB = $"source-b-{Guid.NewGuid():N}";
        var destination = $"destination-{Guid.NewGuid():N}";
        var checkpoints = CreateCheckpointStore();
        await checkpoints.SaveAsync(sourceA, 10, ct);
        await checkpoints.SaveAsync(sourceB, 20, ct);

        var adminA = CreateAdmin();
        var adminB = CreateAdmin();
        var attemptA = adminA.RenameCheckpointAsync(sourceA, destination, ct);
        var attemptB = adminB.RenameCheckpointAsync(sourceB, destination, ct);
        var results = await Task.WhenAll(attemptA, attemptB);

        Assert.Single(results, result => result.Status == CheckpointRenameStatus.Renamed);
        Assert.Single(results, result => result.Status == CheckpointRenameStatus.DestinationExists);

        var destinationPosition = await checkpoints.GetAsync(destination, ct);
        Assert.True(destinationPosition is 10 or 20);

        var sourceAPosition = await checkpoints.GetAsync(sourceA, ct);
        var sourceBPosition = await checkpoints.GetAsync(sourceB, ct);
        if (destinationPosition == 10)
        {
            Assert.Null(sourceAPosition);
            Assert.Equal(20, sourceBPosition);
        }
        else
        {
            Assert.Equal(10, sourceAPosition);
            Assert.Null(sourceBPosition);
        }
    }

    [Fact]
    public async Task ProjectionInspection_UsesTheSingleTenantSchemaWithoutReadingTenantId()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = $"single-projection-{Guid.NewGuid():N}";
        var documentId = $"document-{Guid.NewGuid():N}";

        await using (var conn = await fixture.DataSource.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alberto_projection_states
                    (projection_type, document_id, rebuild_version, state)
                VALUES (@projectionType, @documentId, 1, '{}'::jsonb)
                """;
            cmd.Parameters.AddWithValue("projectionType", projectionType);
            cmd.Parameters.AddWithValue("documentId", documentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var states = await CreateAdmin().GetProjectionStatesAsync(
            projectionType,
            tenant: null,
            search: "document-",
            limit: 20,
            ct);

        var state = Assert.Single(states);
        Assert.Equal(documentId, state.DocumentId);
        Assert.Null(state.TenantId);
    }

    [Fact]
    public async Task SingleTenantTopology_MakesEmptyTenantLeaseInventoryUnambiguous()
    {
        var inventory = await CreateAdmin().GetTenantLeaseInventoryAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(AdminTenancyMode.SingleTenant, inventory.TenancyMode);
        Assert.Empty(inventory.Leases);
    }

    [Fact]
    public async Task TenantFilter_OnSingleTenantStore_IsRejectedInsteadOfSilentlyReturningGlobalRows()
    {
        var act = () => CreateAdmin().GetEventsAsync(
            type: null,
            tag: null,
            tenant: "tenant_a",
            afterPosition: 0,
            limit: 20,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ArgumentException>(act);
        Assert.Equal("tenant", exception.ParamName);
    }
}

/// <summary>
/// A private database on the shared cluster running the shipped multi-tenant migrations,
/// which is the only set that creates <c>alberto_tenant_leases</c>.
/// </summary>
public sealed class PostgresAdminTenantLeaseFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant);

/// <summary>
/// Integration tests for <see cref="PostgresAdminDataAccess.ReleaseTenantLeasesAsync"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresAdminTenantLeaseTests(PostgresAdminTenantLeaseFixture fixture)
    : IClassFixture<PostgresAdminTenantLeaseFixture>
{
    private PostgresAdminDataAccess CreateAdmin() => new(fixture.DataSource);

    private async Task InsertLeaseAsync(string consumerId, string tenantId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alberto_tenant_leases
                (consumer_id, tenant_id, replica_id, expires_at)
            VALUES
                (@consumerId, @tenantId, 'replica-1', now() + interval '5 minutes')
            """,
            conn);
        cmd.Parameters.AddWithValue("consumerId", consumerId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> CountLeasesAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM alberto_tenant_leases", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Both tests below assert on the total lease count, and the leases table has no
    /// per-test key to scope them by — so each starts from an empty table rather than
    /// depending on the order xUnit happens to run them in.
    /// </summary>
    private async Task ClearLeasesAsync(CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM alberto_tenant_leases", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task ReleaseTenantLeasesAsync_NullConsumerId_ReleasesEveryLease()
    {
        // Regression: a null consumerId used to be added with AddWithValue, so it reached
        // PostgreSQL as an untyped NULL. "@consumerId IS NULL" then gave the planner nothing
        // to infer a parameter type from and the statement failed outright with 42P08 —
        // meaning the release-everything call, the one an operator reaches for by default,
        // was the only one that did not work.
        var ct = TestContext.Current.CancellationToken;
        await ClearLeasesAsync(ct);
        await InsertLeaseAsync("consumer-a", $"tenant_{Guid.NewGuid():N}", ct);
        await InsertLeaseAsync("consumer-b", $"tenant_{Guid.NewGuid():N}", ct);

        var released = await CreateAdmin().ReleaseTenantLeasesAsync(consumerId: null, ct);

        Assert.Equal(2, released);
        Assert.Equal(0, await CountLeasesAsync(ct));
    }

    [Fact]
    public async Task ReleaseTenantLeasesAsync_WithConsumerId_ReleasesOnlyThatConsumersLeases()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearLeasesAsync(ct);
        await InsertLeaseAsync("consumer-target", $"tenant_{Guid.NewGuid():N}", ct);
        await InsertLeaseAsync("consumer-other", $"tenant_{Guid.NewGuid():N}", ct);

        var released = await CreateAdmin().ReleaseTenantLeasesAsync("consumer-target", ct);

        Assert.Equal(1, released);
        Assert.Equal(1, await CountLeasesAsync(ct));
    }

    // The operator carries its own copy of this statement rather than delegating to the
    // reader, so it had — and needs — its own coverage: the reader's tests passing said
    // nothing about the path the GraphQL mutation and the MCP tools actually take.

    [Fact]
    public async Task Operator_ReleaseTenantLeasesAsync_NullConsumerId_ReleasesEveryLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearLeasesAsync(ct);
        await InsertLeaseAsync("consumer-a", $"tenant_{Guid.NewGuid():N}", ct);
        await InsertLeaseAsync("consumer-b", $"tenant_{Guid.NewGuid():N}", ct);

        var released = await new PostgresAdminOperator(fixture.DataSource)
            .ReleaseTenantLeasesAsync(consumerId: null, "test-operator", ct);

        Assert.Equal(2, released);
        Assert.Equal(0, await CountLeasesAsync(ct));
    }

    [Fact]
    public async Task Operator_ReleaseTenantLeasesAsync_WithConsumerId_ReleasesOnlyThatConsumersLeases()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearLeasesAsync(ct);
        await InsertLeaseAsync("consumer-target", $"tenant_{Guid.NewGuid():N}", ct);
        await InsertLeaseAsync("consumer-other", $"tenant_{Guid.NewGuid():N}", ct);

        var released = await new PostgresAdminOperator(fixture.DataSource)
            .ReleaseTenantLeasesAsync("consumer-target", "test-operator", ct);

        Assert.Equal(1, released);
        Assert.Equal(1, await CountLeasesAsync(ct));
    }
}
