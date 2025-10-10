using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Projections.Postgres.Migrations;

/// <summary>
/// Hosted service that runs Projection database migrations at application startup.
/// Processes all schemas registered via ProjectionMigrationRegistry.
/// </summary>
public sealed class ProjectionMigrationHostedService(
    ProjectionMigrationRegistry registry,
    ILogger<ProjectionMigrationHostedService> logger)
    : IHostedService
{
    private readonly ILogger<ProjectionMigrationHostedService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ProjectionMigrationRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = _registry.GetRegistrations();

        if (registrations.Count == 0)
        {
            _logger.LogDebug("No Projection schemas registered for migration");
            return;
        }

        _logger.LogInformation("Starting Projection migrations for {Count} schema(s)", registrations.Count);

        foreach (var registration in registrations)
        {
            try
            {
                _logger.LogInformation("Running Projection migration for schema: {Schema}", registration.Schema);

                var migrationRunner = new ProjectionMigrationRunner(
                    registration.ConnectionString,
                    _logger);

                var success = await migrationRunner.MigrateAsync(registration.Schema, cancellationToken);

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Projection migration failed for schema: {registration.Schema}");
                }

                _logger.LogInformation("Projection migration completed successfully for schema: {Schema}",
                    registration.Schema);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to migrate Projection schema: {Schema}", registration.Schema);
                throw;
            }
        }

        _logger.LogInformation("All Projection migrations completed successfully");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to do on shutdown
        return Task.CompletedTask;
    }
}