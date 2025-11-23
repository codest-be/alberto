# Alberto.Projections.InMemory

In-memory implementation of Alberto projection repositories with simplified registration API.

## Installation

```bash
dotnet add package Alberto.Projections.InMemory
```

## Overview

This package provides an in-memory backend for Alberto projections, ideal for:

- **Fast Testing**: Microsecond-level operations, no database setup
- **Component Testing**: Test complete workflows with instant feedback
- **Local Development**: No external dependencies
- **Prototyping**: Quick iteration without database overhead

## Quick Start

### All-In-One Registration (Recommended)

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()
    .WithChannelSubscriptions(channel => channel
        // TKey (Guid) and TState (Order) are automatically inferred!
        .AddInMemoryProjection<OrderProjectionSubscription, OrderProjector>(
            mode: SubscriptionMode.Sync)));
```

### Manual Repository Registration

For manual control:

```csharp
services.AddInMemoryProjectionRepository<Guid, Order, OrderProjector>();
```

## Complete Example

```csharp
// 1. Define your state
public record Order : IHasKey<Guid>, IVersionedProjection
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public OrderStatus Status { get; set; }
    public long GlobalVersion { get; set; }
}

// 2. Create projector
public class OrderProjector : IProjector<Order>
{
    public Order Apply(Order state, object @event)
    {
        return @event switch
        {
            OrderCreated created => new Order
            {
                Id = created.OrderId,
                Amount = created.Amount,
                Status = OrderStatus.Created
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            _ => state
        };
    }
}

// 3. Create subscription
[Subscription("order-projection")]
public class OrderProjectionSubscription(
    IProjectionRepository<Guid, Order> repository,
    OrderProjector projector)
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
        await repository.UpdateWithProjector(
            GetKey(ctx.Event),
            e => projector.Apply(e, ctx.Event),
            ctx.GlobalPosition);
    }

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        await repository.UpdateWithProjector(
            GetKey(ctx.Event),
            e => projector.Apply(e, ctx.Event),
            ctx.GlobalPosition);
    }
}
```

## Swapping Between EF Core and In-Memory

The beauty of the simplified API is that swapping between backends is trivial:

```csharp
// Production: Use EF Core
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithChannelSubscriptions(channel => channel
        .AddEfCoreProjection<OrderProjectionSubscription, OrderDbContext, OrderProjector>(
            mode: SubscriptionMode.Hybrid)));

// Tests: Use In-Memory
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()
    .WithChannelSubscriptions(channel => channel
        .AddInMemoryProjection<OrderProjectionSubscription, OrderProjector>(
            mode: SubscriptionMode.Sync)));  // Sync for immediate updates in tests
```

**Only difference:** `AddEfCoreProjection` vs `AddInMemoryProjection`. Everything else stays the same!

## Performance Characteristics

- **Get**: ~100ns (100,000x faster than PostgreSQL)
- **Upsert**: ~200ns (50,000x faster than PostgreSQL)
- **UpdateWithProjector**: ~300ns
- **Thread-safe**: Uses `ConcurrentDictionary`
- **Memory**: ~1KB per projection

## Features

✅ **Simplified API**: 2 type parameters instead of 5
✅ **Type Inference**: Automatic extraction of TKey and TState
✅ **Thread-Safe**: Safe for concurrent access
✅ **Full Repository API**: Complete `IProjectionRepository<TKey, TState>` implementation
✅ **Automatic Idempotency**: Via `IVersionedProjection.GlobalVersion`
✅ **Zero Setup**: No database or external dependencies

## Production Use

⚠️ **This implementation is NOT suitable for production use.** All data is stored in memory and will be lost when the
application restarts.

For production scenarios, use:

- **[Alberto.Projections.EfCore](../Alberto.Projections.EfCore/)** - EF Core backend with PostgreSQL, SQL Server, MySQL,
  etc.

## See Also

- [Alberto.Projections.EfCore](../Alberto.Projections.EfCore/) - EF Core implementation for production
- [Alberto.EventStore](../../EventStore/Alberto.EventStore/) - Core event store library
- [Example Application](../../Example/) - Complete working example
