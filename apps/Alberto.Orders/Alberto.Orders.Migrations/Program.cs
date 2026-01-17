using Alberto.Orders.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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

/// <summary>
/// Worker that applies EF migrations on startup and then signals completion.
/// </summary>
public class MigrationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MigrationWorker> _logger;

    public MigrationWorker(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        ILogger<MigrationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting EF Core migrations for Orders module...");

            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

            // Ensure schema exists
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE SCHEMA IF NOT EXISTS orders",
                stoppingToken);

            // Apply migrations
            await dbContext.Database.MigrateAsync(stoppingToken);

            _logger.LogInformation("EF Core migrations completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while applying migrations.");
            throw;
        }
        finally
        {
            // Stop the application after migrations complete
            _lifetime.StopApplication();
        }
    }
}
