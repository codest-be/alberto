using Alberto.Orders.Infrastructure.Data;
using Alberto.Orders.Migrations;
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
                logger.LogInformation("Starting EF Core migrations for Orders module...");

                using var scope = serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

                // Ensure schema exists
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE SCHEMA IF NOT EXISTS orders",
                    stoppingToken);

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
