using System.Reflection;
using DbUp;

namespace Alberto.SqlMigrator;

public class Worker(ILogger<Worker> logger, IConfiguration configuration, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting schema migrations...");

            var connectionString = configuration.GetConnectionString("alberto-db");
            if (string.IsNullOrEmpty(connectionString))
            {
                logger.LogError("Connection string 'alberto-db' not found");
                return;
            }

            await RunMigration(connectionString, stoppingToken);

            logger.LogInformation("All migrations completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration failed");
            throw;
        }

        lifetime.StopApplication();
    }

    private async Task RunMigration(string connectionString, CancellationToken cancellationToken)
    {
        var schemas = new[] { "orders", "payments" };

        foreach (var schema in schemas)
        {
            await Task.Run(() =>
            {
                logger.LogInformation("Creating schema '{Schema}' if it doesn't exist...", schema);

                // Create schema first
                CreateSchemaIfNotExists(connectionString, schema);

                // Run the Alberto.EventStore migration for this schema
                var upgrader = DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScriptsEmbeddedInAssembly(typeof(Alberto.Example.TenantContext).Assembly)
                    .WithPreprocessor(new SchemaPreprocessor(schema))
                    .WithExecutionTimeout(TimeSpan.FromMinutes(5))
                    .LogToConsole()
                    .Build();

                var result = upgrader.PerformUpgrade();

                if (!result.Successful)
                {
                    logger.LogError("Migration failed for schema {Schema}: {Error}", schema, result.Error);
                    throw new InvalidOperationException($"Migration failed for schema {schema}", result.Error);
                }

                logger.LogInformation("Migration completed successfully for schema: {Schema}", schema);
            }, cancellationToken);
        }
    }

    private void CreateSchemaIfNotExists(string connectionString, string schema)
    {
        var createSchemaUpgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScript($"CreateSchema_{schema}", $"CREATE SCHEMA IF NOT EXISTS {schema};")
            .WithExecutionTimeout(TimeSpan.FromMinutes(1))
            .LogToConsole()
            .Build();

        var result = createSchemaUpgrader.PerformUpgrade();
        if (!result.Successful)
        {
            logger.LogError("Failed to create schema {Schema}: {Error}", schema, result.Error);
            throw new InvalidOperationException($"Failed to create schema {schema}", result.Error);
        }
    }
}