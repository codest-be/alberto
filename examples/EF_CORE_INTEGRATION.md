# EF Core Integration with Alberto

This guide shows how to use Entity Framework Core for projections instead of the built-in JSONB repository.

## Why EF Core?

**Benefits:**
- ✅ Normalized relational schemas
- ✅ Navigation properties and joins
- ✅ LINQ for complex queries
- ✅ Change tracking
- ✅ Migration management via EF Core CLI
- ✅ Familiar tooling and patterns

**Trade-offs:**
- ❌ Slightly more boilerplate
- ❌ Need to define entity classes and mappings
- ❌ Requires understanding EF Core migrations

---

## Step 1: Define Your DbContext

```csharp
public class OrderReadModelContext : DbContext
{
    public DbSet<OrderReadModel> Orders { get; set; }
    public DbSet<OrderLine> OrderLines { get; set; }
    public DbSet<OrderStatistics> Statistics { get; set; }

    public OrderReadModelContext(DbContextOptions<OrderReadModelContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure schema (separate from EventStore)
        modelBuilder.HasDefaultSchema("read_models");

        // Order entity
        modelBuilder.Entity<OrderReadModel>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(o => o.Id);

            entity.Property(o => o.CustomerId).IsRequired().HasMaxLength(100);
            entity.Property(o => o.Status).IsRequired().HasMaxLength(50);
            entity.Property(o => o.Amount).HasPrecision(18, 2);

            entity.HasIndex(o => o.CustomerId);
            entity.HasIndex(o => o.Status);
            entity.HasIndex(o => o.CreatedAt);

            // Navigation property
            entity.HasMany(o => o.Lines)
                  .WithOne()
                  .HasForeignKey(l => l.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Order line entity
        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.ToTable("order_lines");
            entity.HasKey(l => l.Id);

            entity.Property(l => l.ProductId).IsRequired();
            entity.Property(l => l.Quantity).IsRequired();
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
        });

        // Statistics entity (denormalized for performance)
        modelBuilder.Entity<OrderStatistics>(entity =>
        {
            entity.ToTable("order_statistics");
            entity.HasKey(s => s.Id);

            // Singleton row
            entity.HasData(new OrderStatistics { Id = 1 });
        });
    }
}

// Read models
public class OrderReadModel
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Status { get; set; } = null!;
    public string? TrackingNumber { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }

    // Navigation property
    public List<OrderLine> Lines { get; set; } = new();
}

public class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProductId { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class OrderStatistics
{
    public int Id { get; set; }
    public long TotalOrders { get; set; }
    public long PlacedOrders { get; set; }
    public long ShippedOrders { get; set; }
    public long CancelledOrders { get; set; }
}
```

---

## Step 2: Create Subscription Handlers

```csharp
public class OrderCreatedHandler : IHandleEvent<OrderCreated>
{
    private readonly OrderReadModelContext _context;
    private readonly ILogger<OrderCreatedHandler> _logger;

    public OrderCreatedHandler(
        OrderReadModelContext context,
        ILogger<OrderCreatedHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        var order = new OrderReadModel
        {
            Id = ctx.Event.OrderId,
            CustomerId = ctx.Event.CustomerId,
            Amount = ctx.Event.Amount,
            Status = "Created",
            CreatedAt = ctx.Event.CreatedAt
        };

        _context.Orders.Add(order);
        await _context.SaveChangesAsync(ctx.CancellationToken);

        _logger.LogInformation(
            "Created order read model {OrderId} for customer {CustomerId}",
            order.Id, order.CustomerId);
    }
}

public class OrderPlacedHandler : IHandleEvent<OrderPlaced>
{
    private readonly OrderReadModelContext _context;

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        var order = await _context.Orders.FindAsync(
            new object[] { ctx.Event.OrderId },
            ctx.CancellationToken);

        if (order != null)
        {
            order.Status = "Placed";
            order.PlacedAt = ctx.Event.PlacedAt;
            await _context.SaveChangesAsync(ctx.CancellationToken);
        }
    }
}

public class OrderShippedHandler : IHandleEvent<OrderShipped>
{
    private readonly OrderReadModelContext _context;

    public async Task Handle(EventContext<OrderShipped> ctx)
    {
        var order = await _context.Orders.FindAsync(
            new object[] { ctx.Event.OrderId },
            ctx.CancellationToken);

        if (order != null)
        {
            order.Status = "Shipped";
            order.TrackingNumber = ctx.Event.TrackingNumber;
            order.ShippedAt = ctx.Event.ShippedAt;
            await _context.SaveChangesAsync(ctx.CancellationToken);
        }
    }
}

// Statistics handler (updates aggregate statistics)
public class OrderStatisticsHandler :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>,
    IHandleEvent<OrderShipped>,
    IHandleEvent<OrderCancelled>
{
    private readonly OrderReadModelContext _context;

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        var stats = await GetStatistics(ctx.CancellationToken);
        stats.TotalOrders++;
        await _context.SaveChangesAsync(ctx.CancellationToken);
    }

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        var stats = await GetStatistics(ctx.CancellationToken);
        stats.PlacedOrders++;
        await _context.SaveChangesAsync(ctx.CancellationToken);
    }

    public async Task Handle(EventContext<OrderShipped> ctx)
    {
        var stats = await GetStatistics(ctx.CancellationToken);
        stats.ShippedOrders++;
        await _context.SaveChangesAsync(ctx.CancellationToken);
    }

    public async Task Handle(EventContext<OrderCancelled> ctx)
    {
        var stats = await GetStatistics(ctx.CancellationToken);
        stats.CancelledOrders++;
        await _context.SaveChangesAsync(ctx.CancellationToken);
    }

    private async Task<OrderStatistics> GetStatistics(CancellationToken ct)
    {
        var stats = await _context.Statistics.FindAsync(new object[] { 1 }, ct);
        if (stats == null)
        {
            stats = new OrderStatistics { Id = 1 };
            _context.Statistics.Add(stats);
        }
        return stats;
    }
}
```

---

## Step 3: Register Services

```csharp
public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("alberto-db");

        // Register EF Core DbContext
        services.AddDbContext<OrderReadModelContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    schema: "read_models")));

        // Register Alberto EventStore
        services.AddModule<OrderEventStore>("orders", module => module
            .WithPostgres(options =>
            {
                options.ConnectionString = connectionString;
                options.Schema = "orders";  // Separate schema for events
                options.MigrationStrategy = new ScriptOnlyMigrationStrategy("./migrations");
            })
            .WithMultiTenancy<MultiTenantContext>()
            .WithChannelSubscriptions(channel => channel
                .ConfigureAsync(options =>
                {
                    options.MaxRetries = 5;
                    options.RetryDelayMs = 250;
                })
                // Register EF Core handlers
                .Add<OrderCreatedHandler>(mode: SubscriptionMode.Async)
                .Add<OrderPlacedHandler>(mode: SubscriptionMode.Async)
                .Add<OrderShippedHandler>(mode: SubscriptionMode.Async)
                .Add<OrderCancelledHandler>(mode: SubscriptionMode.Async)
                .Add<OrderStatisticsHandler>(mode: SubscriptionMode.Async))
            .WithCQRS(cqrs => cqrs
                .ScanAssembly(typeof(OrdersModule).Assembly)
                .WithTelemetry())
            .WithTelemetry());

        return services;
    }
}
```

---

## Step 4: EF Core Migrations

```bash
# Add migration
dotnet ef migrations add InitialOrderReadModels \
    --context OrderReadModelContext \
    --output-dir Migrations/ReadModels

# Review generated migration (always review!)
cat Migrations/ReadModels/*_InitialOrderReadModels.cs

# Apply migration (dev)
dotnet ef database update --context OrderReadModelContext

# Generate SQL script (production)
dotnet ef migrations script \
    --context OrderReadModelContext \
    --output migrations/read_models_v1.sql
```

**Generated SQL:**
```sql
CREATE SCHEMA IF NOT EXISTS read_models;

CREATE TABLE read_models.orders (
    id UUID PRIMARY KEY,
    customer_id VARCHAR(100) NOT NULL,
    amount NUMERIC(18,2) NOT NULL,
    status VARCHAR(50) NOT NULL,
    tracking_number VARCHAR(200),
    cancellation_reason TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    placed_at TIMESTAMPTZ,
    shipped_at TIMESTAMPTZ
);

CREATE INDEX ix_orders_customer_id ON read_models.orders (customer_id);
CREATE INDEX ix_orders_status ON read_models.orders (status);
CREATE INDEX ix_orders_created_at ON read_models.orders (created_at);

CREATE TABLE read_models.order_lines (
    id UUID PRIMARY KEY,
    order_id UUID NOT NULL,
    product_id VARCHAR(100) NOT NULL,
    quantity INT NOT NULL,
    unit_price NUMERIC(18,2) NOT NULL,
    CONSTRAINT fk_order_lines_orders FOREIGN KEY (order_id)
        REFERENCES read_models.orders (id) ON DELETE CASCADE
);

CREATE TABLE read_models.order_statistics (
    id INT PRIMARY KEY,
    total_orders BIGINT NOT NULL DEFAULT 0,
    placed_orders BIGINT NOT NULL DEFAULT 0,
    shipped_orders BIGINT NOT NULL DEFAULT 0,
    cancelled_orders BIGINT NOT NULL DEFAULT 0
);

INSERT INTO read_models.order_statistics (id) VALUES (1);
```

---

## Step 5: Query the Read Models

```csharp
public class GetOrderQuery : IQuery<OrderDto>
{
    public Guid OrderId { get; init; }
}

public class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    private readonly OrderReadModelContext _context;

    public GetOrderHandler(OrderReadModelContext context)
    {
        _context = context;
    }

    public async Task<Result<OrderDto>> Handle(
        GetOrderQuery query,
        CancellationToken ct)
    {
        // EF Core query with navigation properties
        var order = await _context.Orders
            .Include(o => o.Lines)  // ✅ Join with order lines
            .FirstOrDefaultAsync(o => o.Id == query.OrderId, ct);

        if (order == null)
            return OrderProblems.OrderNotFound(query.OrderId);

        return new OrderDto
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            Amount = order.Amount,
            Status = order.Status,
            TrackingNumber = order.TrackingNumber,
            Lines = order.Lines.Select(l => new OrderLineDto
            {
                ProductId = l.ProductId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };
    }
}

public class GetOrdersByCustomerQuery : IQuery<List<OrderDto>>
{
    public string CustomerId { get; init; } = null!;
}

public class GetOrdersByCustomerHandler : IQueryHandler<GetOrdersByCustomerQuery, List<OrderDto>>
{
    private readonly OrderReadModelContext _context;

    public async Task<Result<List<OrderDto>>> Handle(
        GetOrdersByCustomerQuery query,
        CancellationToken ct)
    {
        // Complex LINQ query
        var orders = await _context.Orders
            .Where(o => o.CustomerId == query.CustomerId)
            .Where(o => o.Status != "Cancelled")  // ✅ Complex filtering
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return orders.Select(o => new OrderDto
        {
            Id = o.Id,
            CustomerId = o.CustomerId,
            Amount = o.Amount,
            Status = o.Status
        }).ToList();
    }
}
```

---

## Step 6: Schema Separation

Your database now has **two separate schemas**:

### `orders` schema (EventStore - managed by Alberto)
```
orders.events
orders.subscription_checkpoints
orders.subscription_poison_pills
```

### `read_models` schema (Projections - managed by EF Core)
```
read_models.orders
read_models.order_lines
read_models.order_statistics
read_models.__EFMigrationsHistory
```

**Benefits:**
- ✅ Clear separation of concerns
- ✅ Different migration strategies
- ✅ Independent lifecycle
- ✅ EventStore can be replaced without touching projections

---

## Idempotency with EF Core

If you need idempotency (replay protection), track global versions:

```csharp
public class OrderReadModel
{
    public Guid Id { get; set; }
    public long GlobalVersion { get; set; }  // ✅ Track event position
    // ... other properties
}

public class OrderCreatedHandler : IHandleEvent<OrderCreated>
{
    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        // Check if already processed
        var existing = await _context.Orders.FindAsync(ctx.Event.OrderId);
        if (existing != null && existing.GlobalVersion >= ctx.GlobalPosition)
        {
            _logger.LogDebug("Skipping event {Position} - already processed", ctx.GlobalPosition);
            return;  // Skip duplicate
        }

        var order = new OrderReadModel
        {
            Id = ctx.Event.OrderId,
            GlobalVersion = ctx.GlobalPosition,  // ✅ Store position
            // ... other fields
        };

        if (existing == null)
            _context.Orders.Add(order);
        else
            _context.Orders.Update(order);

        await _context.SaveChangesAsync(ctx.CancellationToken);
    }
}
```

---

## Advanced: Batch Updates

For high-throughput scenarios, batch EF Core updates:

```csharp
public class BatchOrderHandler :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>,
    IHandleEvent<OrderShipped>
{
    private readonly OrderReadModelContext _context;
    private readonly List<object> _pendingEvents = new();

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        _pendingEvents.Add(ctx.Event);

        // Flush every 100 events
        if (_pendingEvents.Count >= 100)
        {
            await FlushBatch(ctx.CancellationToken);
        }
    }

    private async Task FlushBatch(CancellationToken ct)
    {
        foreach (var @event in _pendingEvents)
        {
            // Process event
        }

        await _context.SaveChangesAsync(ct);
        _pendingEvents.Clear();
    }
}
```

---

## Comparison: JSONB vs EF Core

| Feature | JSONB Projection | EF Core |
|---------|------------------|---------|
| **Setup** | Simple | More boilerplate |
| **Flexibility** | High (schemaless) | Medium (schema required) |
| **Queries** | Limited | Full LINQ |
| **Joins** | No | Yes |
| **Performance** | Fast (simple queries) | Fast (complex queries) |
| **Migrations** | Auto | EF Core CLI |
| **Tooling** | Limited | Excellent |
| **Type safety** | Runtime | Compile-time |

---

## When to Use Each

### Use JSONB Projection
- ✅ Simple read models (single entity)
- ✅ Rapid prototyping
- ✅ Flexible schemas
- ✅ Document-style queries

### Use EF Core
- ✅ Complex queries with joins
- ✅ Normalized schemas
- ✅ Navigation properties
- ✅ Existing EF Core expertise
- ✅ Advanced LINQ queries
- ✅ Migration management via EF Core CLI

### Use Both (Hybrid)
```csharp
// JSONB for simple queries
.AddPostgresProjection<OrderEventStore, OrderSummary, Guid, Order, OrderSummaryProjector>(...)

// EF Core for complex queries
.Add<OrderDetailsHandler>(...)
```

Best of both worlds!
