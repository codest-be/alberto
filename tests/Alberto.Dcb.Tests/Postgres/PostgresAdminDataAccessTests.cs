using Alberto.Dcb.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Fixture that spins up a real PostgreSQL container and runs the shipped single-tenant
/// migrations. No manual schema patching is applied — the tests exercise the schema
/// exactly as it is delivered to production.
/// </summary>
public sealed class PostgresAdminDataAccessFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();
        var migrationResult = PostgresMigrator.Migrate(connectionString, singleTenant: true);
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// Integration tests for <see cref="PostgresAdminDataAccess"/>, focusing on the composite
/// transactional mutation <see cref="PostgresAdminDataAccess.RetryByRewindAsync"/>.
/// </summary>
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
}
