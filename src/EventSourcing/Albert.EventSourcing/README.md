# Alberto.EventSourcing

Minimal event sourcing building blocks for reconstructing state from events.

## What's Included

- **IProjector<TState>** - Interface for projecting events into state
- **ProjectorExtensions.Evolve()** - Helper method to fold events into state
- **EventStoreExtensions** - Convenient Load/Persist methods for EventStore

## Usage

### Define a Projector

```csharp
public record ShoppingCartState
{
    public Guid Id { get; init; }
    public Dictionary<Guid, int> Items { get; init; } = new();
    public bool IsCheckedOut { get; init; }
}

public class ShoppingCartProjector : IProjector<ShoppingCartState>
{
    public ShoppingCartState Apply(ShoppingCartState state, object @event)
    {
        return @event switch
        {
            ShoppingCartCreated created => state with { Id = created.CartId },
            ProductAddedToCart added => state with
            {
                Items = AddItem(state.Items, added.ProductId, added.Quantity)
            },
            ShoppingCartCheckedOut => state with { IsCheckedOut = true },
            _ => state
        };
    }
}
```

### Load and Evolve State

```csharp
var projector = new ShoppingCartProjector();
var query = new StreamQuery().WithTags(new EventTag("cart", cartId));

// Load events from EventStore
var (events, lastEventId) = await eventStore.Load(query, ct);

// Evolve events into state
var state = projector.Evolve(events);
```

### Persist New Events

```csharp
var newEvents = new object[]
{
    new ProductAddedToCart { CartId = id, ProductId = productId, Quantity = 1 }
};

await eventStore.Persist(query, lastEventId, newEvents, ct);
```

## Design Philosophy

This library provides **only the core event sourcing concepts**:

- Projecting events into state
- Loading/persisting events with optimistic concurrency

It does **not** include:

- CQRS framework (commands, queries, handlers)
- Validation
- Result types
- Auto-registration

For a full CQRS framework, see **Alberto.CQRS**.

## Integration

Works seamlessly with Alberto.EventStore:

```csharp
public class OrderEventStore : EventStoreFactory
{
    public OrderEventStore(
        ITenantContext tenantContext,
        IEventStoreBackend backend,
        IDiagnosticsEventListener? diagnostics = null)
        : base(tenantContext, backend, diagnostics)
    {
    }
}

// In your handler/service
public class MyService(OrderEventStore eventStore)
{
    public async Task DoWork()
    {
        var (events, version) = await eventStore.Load(query, ct);
        // ... work with events
    }
}
```
