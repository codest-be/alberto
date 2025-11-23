# Alberto.Projections.EfCore

Entity Framework Core adapter for Alberto EventStore projections. Provides simplified projection registration
with automatic repository setup and type inference.

## Why Use This?

**Simplified API**: Register projections with minimal type parameters - `TKey` and `TState` are automatically inferred
from your subscription interface.

```csharp
// Before: 7 type parameters
.AddEfCoreProjection<OrderEventStore, OrderProjectionSubscription, OrderDbContext, Guid, Order, OrderProjector>(...)

// After: 3 type parameters (TKey and TState inferred automatically)
.AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(...)
```

**Easy Testing**: Swap between EF Core (production) and in-memory (tests) without changing your projection handlers.

```csharp
// Production
.AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(mode: SubscriptionMode.Sync)

// Tests
.AddInMemoryProjection<OrderProjectionSubscription, OrderProjector>(mode: SubscriptionMode.Sync)
```

## Installation

```bash
dotnet add package Alberto.Projections.EfCore
```

## Quick Start

### 1. Define Your Projection State

```csharp
public class Order : IHasKey<Guid>, IVersionedProjection
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = null!;
    public decimal Amount { get; set; }
    public OrderStatus Status { get; set; }

    // For idempotency - tracks last event position
    public long GlobalVersion { get; set; }
}
```

### 2. Configure Your DbContext

```csharp
public class OrderDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CustomerId).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.GlobalVersion); // For idempotency checks
        });
    }
}
```

### 3. Create Projector

```csharp
public class OrderProjector : IProjector<Order>
{
    public Order Apply(Order state, object @event)
    {
        return @event switch
        {
            OrderCreated created => new Order
            {
                Id = created.OrderId,
                CustomerId = created.CustomerId,
                Amount = created.Amount,
                Status = OrderStatus.Created
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            _ => state
        };
    }
}
```

### 4. Create Projection Subscription

```csharp
[Subscription("order-projection")]
public class OrderProjectionSubscription(IProjectionRepository<Guid, Order> repository, OrderProjector projector)
    : IProjectionSubscription<Guid, Order>,
      IHandleEvent<OrderCreated>,
      IHandleEvent<OrderPlaced>
{
    public Guid GetKey(object @event) => @event switch
    {
        OrderCreated e => e.OrderId,
        OrderPlaced e => e.OrderId,
        _ => throw new InvalidOperationException()
    };

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        await repository.UpdateWithProjector(GetKey(ctx.Event), e => projector.Apply(e, ctx.Event), ctx.GlobalPosition);
    }

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        await repository.UpdateWithProjector(GetKey(ctx.Event), e => projector.Apply(e, ctx.Event), ctx.GlobalPosition);
    }
}
```

### 5. Register Everything (All-In-One)

```csharp
services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithChannelSubscriptions(channel => channel
        // TKey (Guid) and TState (Order) are automatically inferred!
        .AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(
            mode: SubscriptionMode.Hybrid)));
```

**What this does automatically:**

- ✅ Registers `IProjectionRepository<Guid, Order>` with EF Core backend
- ✅ Registers `OrderProjector` as scoped service
- ✅ Registers `OrderProjectionSubscription` with the subscription system
- ✅ Infers `TKey=Guid` and `TState=Order` from `IProjectionSubscription<Guid, Order>`

## Testing

### Swap to In-Memory for Fast Tests

Simply replace `AddEfCoreProjection` with `AddInMemoryProjection`:

```csharp
// Production (EF Core)
.AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(
    mode: SubscriptionMode.Hybrid)

// Tests (In-Memory)
.AddInMemoryProjection<OrderProjectionSubscription, OrderProjector>(
    mode: SubscriptionMode.Sync)  // Use Sync for immediate updates in tests
```

### Manual Repository Registration (if needed)

If you're not using subscriptions or need more control:

```csharp
// Production
services.AddEfCoreProjectionRepository<OrderDbContext, Guid, Order>();

// Tests
services.AddSingleton<IProjectionRepository<Guid, Order>,
                      InMemoryProjectionRepository<Guid, Order>>();
```

## How Type Inference Works

The `AddEfCoreProjection` method uses reflection to automatically extract `TKey` and `TState` from your subscription:

```csharp
// Your subscription implements this interface
public class OrderProjectionSubscription : IProjectionSubscription<Guid, Order>

// AddEfCoreProjection extracts:
// - TKey = Guid
// - TState = Order
// Using reflection on the IProjectionSubscription<TKey, TState> interface
```

This means you only need to specify:

1. **TSubscription** - Your subscription class
2. **TDbContext** - Your EF Core DbContext
3. **TProjector** - Your projector implementation

Everything else is inferred automatically!

## Idempotency

The `IVersionedProjection` interface provides automatic idempotency:

```csharp
public interface IVersionedProjection
{
    long GlobalVersion { get; set; }
}
```

When you use `UpdateWithProjector`, the repository:

1. Checks if `existing.GlobalVersion >= globalPosition`
2. If true, skips the update (already processed)
3. If false, applies the projector and sets `GlobalVersion = globalPosition`

This ensures events are never processed twice, even if replayed.

## API Options

### Option 1: Simplified All-In-One (Recommended)

Use `AddEfCoreProjection` for minimal boilerplate:

```csharp
.AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(
    mode: SubscriptionMode.Hybrid)
```

**Pros:**

- ✅ Minimal type parameters (3 instead of 7)
- ✅ Automatic type inference
- ✅ One-line registration
- ✅ Easy to swap with `AddInMemoryProjection` for tests

### Option 2: Manual Registration

For more control, register components separately:

```csharp
services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(connectionString));
services.AddEfCoreProjectionRepository<OrderDbContext, Guid, Order>();
services.AddScoped<OrderProjector>();

// Then register subscription manually
.AddProjection<OrderProjectionSubscription, Guid, Order>(mode: SubscriptionMode.Hybrid)
```

**Use when:**

- ✅ Sharing repository across multiple subscriptions
- ✅ Need custom repository configuration
- ✅ Using projector outside subscriptions

Both approaches work great with Alberto!

## Multiple Projections Example

You can register multiple projections easily:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithChannelSubscriptions(channel => channel
        // Per-order projection (sync for strong consistency)
        .AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(
            mode: SubscriptionMode.Hybrid)

        // Global statistics (async for better performance)
        .AddEfCoreProjection<OrderStatisticsSubscription, OrderDbContext, OrderStatisticsProjector>(
            mode: SubscriptionMode.Async)

        // Customer summary (async)
        .AddEfCoreProjection<CustomerSummarySubscription, OrderDbContext, CustomerSummaryProjector>(
            mode: SubscriptionMode.Async)
    ));
```

Each projection automatically gets its own repository with the correct types inferred!

## Key Benefits Summary

✅ **Simplified API**: 3 type parameters instead of 7
✅ **Type Inference**: Automatic extraction of TKey and TState
✅ **Easy Testing**: Swap `AddEfCoreProjection` ↔ `AddInMemoryProjection`
✅ **Automatic Idempotency**: Via `IVersionedProjection.GlobalVersion`
✅ **Flexible**: Use all-in-one or manual registration
✅ **Multiple Projections**: Register many projections with minimal code

## See Also

- [Alberto.Projections.InMemory](../Alberto.Projections.InMemory/) - In-memory implementation for testing
- [Alberto.EventStore](../../EventStore/Alberto.EventStore/) - Core event store library
- [Example Application](../../Example/) - Complete working example
