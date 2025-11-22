using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Postgres.Migrations;
using Alberto.EventStore.Tests.Specifications;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.EventStore.Tests.Postgres;

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
        PostgresEventStoreBackend backend = new(options, _logger);
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

    private readonly object _tenantIdLock = new();

    private int _nextTenantId = 2000; // Start from 1000 to avoid conflicts with hardcoded tenant IDs
    public PostgresEventStoreOptions Options { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        Options = new PostgresEventStoreOptions { ConnectionString = _postgresContainer.GetConnectionString(), Schema = "orders", BulkInsertThreshold = 5 };

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
            await using NpgsqlConnection connection = new(Options.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                $"DELETE FROM {Options.Schema}.events WHERE tenant_id = @tenantId",
                new { tenantId });
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
                await using NpgsqlConnection connection = new(Options.ConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    $"DELETE FROM {Options.Schema}.events WHERE tenant_id = ANY(@tenantIds)",
                    new { tenantIds });
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
    }

    private async Task RunMigrations()
    {
        // Clear any existing registrations (important for test isolation)
        PostgresSchemaRegistry.Instance.Clear();

        // Register schema in singleton registry with module key
        var moduleKey = "TestModule";
        PostgresSchemaRegistry.Instance.Register(moduleKey, Options.Schema, Options.ConnectionString);

        // Set up a minimal service provider with the options for the migration service
        var services = new ServiceCollection();
        services.AddSingleton(PostgresSchemaRegistry.Instance);
        services.Configure<PostgresEventStoreOptions>(moduleKey, opts =>
        {
            opts.ConnectionString = Options.ConnectionString;
            opts.Schema = Options.Schema;
        });

        var serviceProvider = services.BuildServiceProvider();

        // Create and run the migration service
        var logger = new NullLogger<MigrationHostedService>();
        var optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<PostgresEventStoreOptions>>();
        var migrationService = new MigrationHostedService(PostgresSchemaRegistry.Instance, optionsMonitor, logger);

        await migrationService.StartingAsync(CancellationToken.None);

        await VerifySchemaSetup();
    }

    private async Task VerifySchemaSetup()
    {
        await using NpgsqlConnection connection = new(Options.ConnectionString);
        await connection.OpenAsync();

        // Verify table exists in correct schema
        var tableExists = await connection.QuerySingleAsync<bool>(
            @"
        SELECT EXISTS (
            SELECT 1 FROM information_schema.tables 
            WHERE table_schema = @Schema AND table_name = 'events'
        )",
            new { Options.Schema });

        if (!tableExists)
            throw new InvalidOperationException($"Events table not found in schema '{Options.Schema}'");

        // Verify indexes exist
        var indexCount = await connection.QuerySingleAsync<int>(
            @"
        SELECT COUNT(*) 
        FROM pg_indexes 
        WHERE schemaname = @Schema AND tablename = 'events'",
            new { Options.Schema });

        Console.WriteLine($"Created {indexCount} indexes in schema '{Options.Schema}'");
    }
}