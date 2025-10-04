using DbUp;
using DbUp.Engine;

namespace Alberto.SqlMigrator;

public class Worker(ILogger<Worker> logger, IConfiguration configuration, IHostApplicationLifetime lifetime)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting schema migrations...");

            string? connectionString = configuration.GetConnectionString("alberto-db");
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
        string[] schemas = ["orders", "payments"];

        foreach (string schema in schemas)
            await Task.Run(() =>
            {
                logger.LogInformation("Creating schema '{Schema}' if it doesn't exist...", schema);

                // Create schema first
                CreateSchemaIfNotExists(connectionString, schema);

                // Run the Alberto.EventStore migration for this schema
                UpgradeEngine? upgrader = DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScriptsEmbeddedInAssembly(typeof(Example.Modules.Orders.OrdersModule).Assembly)
                    .WithPreprocessor(new SchemaPreprocessor(schema))
                    .WithExecutionTimeout(TimeSpan.FromMinutes(5))
                    .LogToConsole()
                    .Build();

                DatabaseUpgradeResult? result = upgrader.PerformUpgrade();

                if (!result.Successful)
                {
                    logger.LogError("Migration failed for schema {Schema}: {Error}", schema, result.Error);
                    throw new InvalidOperationException($"Migration failed for schema {schema}", result.Error);
                }

                logger.LogInformation("Migration completed successfully for schema: {Schema}", schema);
            }, cancellationToken);
    }

    private void CreateSchemaIfNotExists(string connectionString, string schema)
    {
        UpgradeEngine? createSchemaUpgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScript($"CreateSchema_{schema}", $"CREATE SCHEMA IF NOT EXISTS {schema};")
            .WithExecutionTimeout(TimeSpan.FromMinutes(1))
            .LogToConsole()
            .Build();

        DatabaseUpgradeResult? result = createSchemaUpgrader.PerformUpgrade();
        if (!result.Successful)
        {
            logger.LogError("Failed to create schema {Schema}: {Error}", schema, result.Error);
            throw new InvalidOperationException($"Failed to create schema {schema}", result.Error);
        }
    }
}