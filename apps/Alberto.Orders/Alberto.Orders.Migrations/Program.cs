using Alberto.Dcb.Postgres;
using Alberto.Orders.Migrations;
using Alberto.Orders.Platform;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Register DbContext for migrations
builder.Services.AddDbContext<OrdersDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("alberto");
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));
});

builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();

namespace Alberto.Orders.Migrations
{
    /// <summary>
    /// Worker that applies EF migrations on startup and then signals completion.
    /// </summary>
    public class MigrationWorker(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        ILogger<MigrationWorker> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
                    .GetConnectionString("alberto")
                    ?? throw new InvalidOperationException("Connection string 'alberto' not found");

                // Run DCB schema migrations first (single process, no race condition)
                logger.LogInformation("Starting DCB migrations for Orders module...");
                var dcbResult = PostgresMigrator.Migrate(connectionString, schema: "orders", singleTenant: false);
                if (!dcbResult.Successful)
                    throw new InvalidOperationException($"DCB migration failed: {dcbResult.Error?.Message}", dcbResult.Error);
                logger.LogInformation("DCB migrations completed ({Count} scripts applied).", dcbResult.ExecutedScripts.Count);

                // Run EF Core migrations
                logger.LogInformation("Starting EF Core migrations for Orders module...");

                using var scope = serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

                // Apply migrations
                await dbContext.Database.MigrateAsync(stoppingToken);

                logger.LogInformation("EF Core migrations completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while applying migrations.");
                throw;
            }
            finally
            {
                // Stop the application after migrations complete
                lifetime.StopApplication();
            }
        }
    }
}
