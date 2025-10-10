using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Hosted service that runs EventStore database migrations at application startup.
/// Processes all schemas registered via EventStoreMigrationRegistry.
/// </summary>
public sealed class EventStoreMigrationHostedService(
    EventStoreMigrationRegistry registry,
    ILogger<EventStoreMigrationHostedService> logger)
    : IHostedService
{
    private readonly ILogger<EventStoreMigrationHostedService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly EventStoreMigrationRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = _registry.GetRegistrations();

        if (registrations.Count == 0)
        {
            _logger.LogDebug("No EventStore schemas registered for migration");
            return;
        }

        _logger.LogInformation("Starting EventStore migrations for {Count} schema(s)", registrations.Count);

        foreach (var registration in registrations)
        {
            try
            {
                _logger.LogInformation("Running EventStore migration for schema: {Schema}", registration.Schema);

                var migrationRunner = new EventStoreMigrationRunner(
                    registration.ConnectionString,
                    _logger);

                var success = await migrationRunner.MigrateAsync(registration.Schema, cancellationToken);

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"EventStore migration failed for schema: {registration.Schema}");
                }

                _logger.LogInformation("EventStore migration completed successfully for schema: {Schema}",
                    registration.Schema);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate EventStore schema: {Schema}", registration.Schema);
                throw;
            }
        }

        _logger.LogInformation("All EventStore migrations completed successfully");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to do on shutdown
        return Task.CompletedTask;
    }
}