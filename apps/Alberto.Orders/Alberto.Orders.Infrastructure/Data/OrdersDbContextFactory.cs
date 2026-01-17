using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Alberto.Orders.Infrastructure.Data;

/// <summary>
/// Design-time factory for OrdersDbContext.
/// Used by EF Core tools for migrations.
/// </summary>
public class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<OrdersDbContext>();

        // Use a default connection string for design-time operations
        // This is only used by EF tooling, not at runtime
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__alberto")
            ?? "Host=localhost;Database=alberto;Username=postgres;Password=postgres";

        optionsBuilder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orders"));

        return new OrdersDbContext(optionsBuilder.Options);
    }
}
