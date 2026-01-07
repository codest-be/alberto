using Alberto.Dcb.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// PostgreSQL implementation of the event store backend specification tests.
/// Uses Testcontainers to spin up a real PostgreSQL instance.
/// </summary>
public sealed class PostgresEventStoreBackendTests : EventStoreBackendSpecification, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private NpgsqlDataSource? _dataSource;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Run migrations
        var migrationResult = PostgresMigrator.Migrate(_container.GetConnectionString());
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        if (_dataSource == null)
        {
            throw new InvalidOperationException("Data source not initialized. Ensure InitializeAsync was called.");
        }

        return Task.FromResult<IEventStoreBackend>(
            new PostgresEventStoreBackend(_dataSource, TimeProvider));
    }

    protected override async Task CleanupAsync()
    {
        if (_dataSource == null) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE event_tag_positions, event_type_positions, events RESTART IDENTITY CASCADE",
            connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
