# Alberto.Projections.EfCore

Entity Framework Core adapter for Alberto EventStore projections. Provides easy integration between
`IProjectionRepository<TKey, TState>` and EF Core `DbContext` for testable, swappable projection storage.

## Why Use This?

**Easy Testing**: Swap between EF Core (production) and in-memory (tests) without changing your projection handlers.

```csharp
// Production
services.AddEfCoreProjectionRepository<OrderDbContext, Guid, OrderSummary>();

// Tests
services.AddSingleton<IProjectionRepository<Guid, OrderSummary>,
                      InMemoryProjectionRepository<Guid, OrderSummary>>();
```

## Installation

```bash
dotnet add package Alberto.Projections.EfCore
```

## Quick Start

### 1. Define Your Projection Entity

```csharp
public class OrderSummary : IHasKey<Guid>, IVersionedProjection
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = null!;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = null!;

    // For idempotency - tracks last event position
    public long GlobalVersion { get; set; }
}
```

### 2. Configure Your DbContext

```csharp
public class OrderDbContext : DbContext
{
    public DbSet<OrderSummary> OrderSummaries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderSummary>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerId).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.GlobalVersion); // For idempotency checks
        });
    }
}
```

### 3. Register Services

```csharp
services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

// Register the projection repository
services.AddEfCoreProjectionRepository<OrderDbContext, Guid, OrderSummary>();
```

### 4. Create Projection Handler

```csharp
public class OrderSummaryHandler : IHandleEvent<OrderCreated>, IHandleEvent<OrderPlaced>
{
    private readonly IProjectionRepository<Guid, OrderSummary> _repository;

    public OrderSummaryHandler(IProjectionRepository<Guid, OrderSummary> repository)
    {
        _repository = repository;
    }

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        var summary = new OrderSummary
        {
            Id = ctx.Event.OrderId,
            CustomerId = ctx.Event.CustomerId,
            TotalAmount = ctx.Event.Amount,
            Status = "Created"
        };

        // Automatically handles idempotency via GlobalVersion
        await _repository.Upsert(ctx.Event.OrderId, summary, ctx.GlobalPosition);
    }

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        var summary = await _repository.Get(ctx.Event.OrderId);
        if (summary != null)
        {
            summary.Status = "Placed";
            await _repository.Upsert(ctx.Event.OrderId, summary, ctx.GlobalPosition);
        }
    }
}
```

### 5. Register Subscription

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithChannelSubscriptions(channel => channel
        .Add<OrderSummaryHandler>(mode: SubscriptionMode.Async)));
```

## Testing

### In-Memory for Fast Tests

```csharp
services.AddSingleton<IProjectionRepository<Guid, OrderSummary>,
                      InMemoryProjectionRepository<Guid, OrderSummary>>();
```

### EF Core In-Memory Provider

```csharp
services.AddDbContext<OrderDbContext>(options =>
    options.UseInMemoryDatabase("TestDb"));

services.AddEfCoreProjectionRepository<OrderDbContext, Guid, OrderSummary>();
```

## Idempotency

The `IVersionedProjection` interface provides automatic idempotency:

```csharp
public interface IVersionedProjection
{
    long GlobalVersion { get; set; }
}
```

When you call `Upsert`, the repository:

1. Checks if `existing.GlobalVersion >= globalPosition`
2. If true, skips the update (already processed)
3. If false, updates the projection and sets `GlobalVersion = globalPosition`

This ensures events are never processed twice, even if replayed.

## When to Use This vs Plain DbContext

**Use `IProjectionRepository` (this package) when:**

- ✅ Simple, single-entity projections
- ✅ Want easy testing (swap to InMemory)
- ✅ Need automatic idempotency

**Use plain `DbContext` when:**

- ✅ Complex projections with navigation properties
- ✅ Need LINQ queries across multiple entities
- ✅ Using EF Core In-Memory provider for tests anyway

Both approaches work great with Alberto!

## Example: Complex Projection with Plain DbContext

For projections with relationships, use `DbContext` directly:

```csharp
public class OrderDetailsHandler : IHandleEvent<OrderCreated>
{
    private readonly OrderDbContext _context;

    public OrderDetailsHandler(OrderDbContext context)
    {
        _context = context;
    }

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        var order = new Order
        {
            Id = ctx.Event.OrderId,
            CustomerId = ctx.Event.CustomerId,
            Lines = ctx.Event.Lines.Select(l => new OrderLine
            {
                ProductId = l.ProductId,
                Quantity = l.Quantity
            }).ToList(),
            GlobalVersion = ctx.GlobalPosition
        };

        // Check idempotency manually
        var existing = await _context.Orders.FindAsync(ctx.Event.OrderId);
        if (existing != null && existing.GlobalVersion >= ctx.GlobalPosition)
            return; // Already processed

        _context.Orders.Add(order);
        await _context.SaveChangesAsync();
    }
}
```

## See Also

- [EF_CORE_INTEGRATION.md](../../../examples/EF_CORE_INTEGRATION.md) - Complete guide to EF Core projections
- [Alberto.Projections.InMemory](../Alberto.Projections.InMemory/) - In-memory implementation for testing
