using Alberto.Dcb.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Shared PostgreSQL container fixture for all tests.
/// Created once per test collection, reused across all tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var migrationResult = PostgresMigrator.Migrate(_container.GetConnectionString());
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
