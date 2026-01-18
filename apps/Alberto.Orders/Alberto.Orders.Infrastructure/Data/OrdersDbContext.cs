using Alberto.Orders.Core.Order;
using Alberto.Orders.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Orders.Infrastructure.Data;

/// <summary>
/// DbContext for Orders module EF projections.
/// </summary>
public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Order summary entities.
    /// </summary>
    public DbSet<OrderSummaryEntity> OrderSummaries => Set<OrderSummaryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");

        modelBuilder.Entity<OrderSummaryEntity>(entity =>
        {
            entity.ToTable("order_summaries");

            // Composite primary key for tenant isolation with rebuild version support
            entity.HasKey(e => new { e.TenantId, e.DocumentId, e.RebuildVersion });

            // Property configurations
            entity.Property(e => e.TenantId)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.DocumentId)
                .HasMaxLength(1000)
                .IsRequired();

            entity.Property(e => e.OrderId)
                .IsRequired();

            entity.Property(e => e.CustomerId)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.Total)
                .HasPrecision(18, 2);

            entity.Property(e => e.Notes)
                .HasMaxLength(2000);

            entity.Property(e => e.TrackingNumber)
                .HasMaxLength(200);

            entity.Property(e => e.Carrier)
                .HasMaxLength(200);

            entity.Property(e => e.CancellationReason)
                .HasMaxLength(2000);

            // Indexes for efficient queries
            entity.HasIndex(e => new { e.TenantId, e.Status })
                .HasDatabaseName("ix_order_summaries_tenant_status");

            entity.HasIndex(e => new { e.TenantId, e.CustomerId })
                .HasDatabaseName("ix_order_summaries_tenant_customer");

            entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
                .HasDatabaseName("ix_order_summaries_tenant_created");

            entity.HasIndex(e => new { e.TenantId, e.UpdatedAt })
                .HasDatabaseName("ix_order_summaries_tenant_updated");

            entity.HasIndex(e => new { e.TenantId, e.OrderId, e.RebuildVersion })
                .HasDatabaseName("ix_order_summaries_tenant_orderid_version")
                .IsUnique();

            // Idempotency: track last processed event position
            entity.Property(e => e.LastProcessedPosition)
                .HasDefaultValue(0L);

            // Rebuild version for zero-downtime rebuilds
            entity.Property(e => e.RebuildVersion)
                .HasDefaultValue(1);

            // Optimistic concurrency: use PostgreSQL xmin system column
            // xmin is a system column - EF reads it via RETURNING clause, not INSERT
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            // Line items stored as JSON column
            entity.OwnsMany(e => e.LineItems, li => li.ToJson());
        });
    }
}
