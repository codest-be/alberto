using Dapper;
using EventStore;
using EventStore.MultiTenant;
using EventStore.Postgres;
using Eventstore.Tests.Specifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Eventstore.Tests.Postgres;

/// <summary>
///     PostgreSQL specification tests using Testcontainers
///     using a shared container across multiple test classes
/// </summary>
[Collection("Postgres Integration Tests")]
public class PostgresEventStoreBackendSpecificationTests(PostgresTestFixture fixture)
    : EventStoreBackendSpecification
{
    private readonly ILogger<PostgresEventStoreBackend> _logger =
        new NullLoggerFactory().CreateLogger<PostgresEventStoreBackend>();

    private readonly int _testTenantId = fixture.GetNextTenantId(); // Get unique tenant ID

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        var options = Options.Create(fixture.Options);
        var backend = new PostgresEventStoreBackend(options, _logger);
        return Task.FromResult<IEventStoreBackend>(backend);
    }

    protected override Tenant CurrentTenant()
    {
        return new Tenant(_testTenantId.ToString());
    }

    protected override Task SetupAsync()
    {
        return Task.CompletedTask;
    }

    protected override async Task CleanupAsync()
    {
        // Clean up the specific tenant for this test
        await fixture.CleanupTestData(_testTenantId);
    }
}

/// <summary>
///     Shared test fixture for PostgreSQL integration tests
///     One container shared across multiple test classes
/// </summary>
[CollectionDefinition("Postgres Integration Tests")]
public class PostgresIntegrationTestCollection : ICollectionFixture<PostgresTestFixture>
{
}

public class PostgresTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("eventstore_test")
        .WithUsername("test_user")
        .WithPassword("test_password")
        .WithCleanUp(true)
        .Build();

    private readonly object _tenantIdLock = new object();

    private int _nextTenantId = 2000; // Start from 1000 to avoid conflicts with hardcoded tenant IDs
    public PostgresEventStoreOptions Options { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        Options = new PostgresEventStoreOptions
        {
            ConnectionString = _postgresContainer.GetConnectionString(),
            Schema = "app",
            BulkInsertThreshold = 5
        };

        await RunMigrations();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
    }

    /// <summary>
    ///     Gets a unique tenant ID for each test
    ///     Thread-safe for parallel test execution
    /// </summary>
    public int GetNextTenantId()
    {
        lock (_tenantIdLock)
        {
            return _nextTenantId++;
        }
    }

    public async Task CleanupTestData(int tenantId)
    {
        try
        {
            await using var connection = new NpgsqlConnection(Options.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                $"DELETE FROM {Options.Schema}.events WHERE tenant_id = @tenantId",
                new
                {
                    tenantId
                });
        }
        catch (Exception)
        {
            // Ignore cleanup errors in tests
        }
    }

    /// <summary>
    ///     Clean up multiple tenants (for multi-tenant tests)
    /// </summary>
    public async Task CleanupTestData(params int[] tenantIds)
    {
        if (tenantIds?.Length > 0)
            try
            {
                await using var connection = new NpgsqlConnection(Options.ConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    $"DELETE FROM {Options.Schema}.events WHERE tenant_id = ANY(@tenantIds)",
                    new
                    {
                        tenantIds
                    });
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
    }

    private async Task RunMigrations()
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {Options.Schema}");

        var migrationSql = await LoadMigrationFromFile();
        await connection.ExecuteAsync(migrationSql);
        await VerifySchemaSetup();
    }

    private async Task VerifySchemaSetup()
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync();

        // Verify table exists in correct schema
        var tableExists = await connection.QuerySingleAsync<bool>(
            @"
        SELECT EXISTS (
            SELECT 1 FROM information_schema.tables 
            WHERE table_schema = @Schema AND table_name = 'events'
        )",
            new
            {
                Options.Schema
            });

        if (!tableExists)
            throw new InvalidOperationException($"Events table not found in schema '{Options.Schema}'");

        // Verify indexes exist
        var indexCount = await connection.QuerySingleAsync<int>(
            @"
        SELECT COUNT(*) 
        FROM pg_indexes 
        WHERE schemaname = @Schema AND tablename = 'events'",
            new
            {
                Options.Schema
            });

        Console.WriteLine($"Created {indexCount} indexes in schema '{Options.Schema}'");
    }

    private async Task<string> LoadMigrationFromFile()
    {
        // Get the solution directory by going up from the test project
        var currentDirectory = AppContext.BaseDirectory;
        var solutionDirectory = Directory.GetParent(currentDirectory);

        // Navigate up until we find the solution root (contains EventStore.Postgres folder)
        while (solutionDirectory != null
               && !Directory.Exists(Path.Combine(solutionDirectory.FullName, "EventStore.Postgres")))
            solutionDirectory = solutionDirectory.Parent;

        if (solutionDirectory == null)
            throw new DirectoryNotFoundException(
                "Could not locate the solution root directory containing EventStore.Postgres");

        var migrationPath = Path.Combine(
            solutionDirectory.FullName,
            "EventStore.Postgres",
            "Migrations",
            "CreateEventStoreSchema.sql");

        if (!File.Exists(migrationPath))
            throw new FileNotFoundException($"Migration file not found: {migrationPath}");

        return await File.ReadAllTextAsync(migrationPath);
    }
}