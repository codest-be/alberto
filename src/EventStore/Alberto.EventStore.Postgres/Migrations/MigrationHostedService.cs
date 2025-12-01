using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Runtime migration service that uses pluggable IMigrationStrategy for EventStore schema management.
/// Uses IHostedLifecycleService to ensure migrations complete before other services start.
///
/// IMPORTANT: For production deployments, use NoMigrationStrategy and manage migrations externally.
/// </summary>
public sealed class MigrationHostedService(
    PostgresSchemaRegistry schemaRegistry,
    IOptionsMonitor<PostgresEventStoreOptions> optionsMonitor,
    ILogger<MigrationHostedService> logger)
    : IHostedLifecycleService
{
    private readonly ILogger<MigrationHostedService>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IOptionsMonitor<PostgresEventStoreOptions> _optionsMonitor =
        optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));

    private readonly PostgresSchemaRegistry _schemaRegistry =
        schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting EventStore schema migrations");

        try
        {
            // Get schemas from registry (populated during module registration)
            var schemaModuleMap = _schemaRegistry.GetAllWithModuleKeys();

            if (schemaModuleMap.Count == 0)
            {
                _logger.LogWarning("No EventStore schemas registered");
                return;
            }

            _logger.LogInformation("Discovered {Count} EventStore schema(s): {Schemas}",
                schemaModuleMap.Count, string.Join(", ", schemaModuleMap.Select(x => x.Value.Schema)));

            // Run migrations for each schema using configured strategy
            foreach (var (moduleKey, (schema, connectionString)) in schemaModuleMap.OrderBy(kv => kv.Value.Schema))
            {
                _logger.LogInformation("Processing schema '{Schema}' with module key '{ModuleKey}'", schema, moduleKey);

                var options = _optionsMonitor.Get(moduleKey);
                var strategy = options.MigrationStrategy;

                _logger.LogInformation("Using migration strategy: {Strategy}", strategy.GetType().Name);

                if (options.MigrationsDirectory != null)
                {
                    _logger.LogInformation("Using migrations directory: {Directory}", options.MigrationsDirectory);
                }

                await strategy.EnsureSchemaAsync(schema, connectionString, options.MigrationsDirectory, cancellationToken);
            }

            _logger.LogInformation("EventStore schema migrations completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during EventStore schema migrations");
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}