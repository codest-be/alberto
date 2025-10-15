# Alberto.EventStore

Core event store library for .NET with multi-tenant and multi-schema support.

## Installation

```bash
dotnet add package Alberto.EventStore
```

## Overview

Alberto.EventStore provides a high-performance event store abstraction with support for:

- Multi-tenant event storage
- Stream-based event persistence
- Event subscriptions and replays
- Tag-based querying
- Pluggable backend implementations (In-Memory, PostgreSQL)

## Quick Start

```csharp
using Alberto.EventStore;

// Configure services
services.AddEventStore()
        .AddInMemoryEventStore(); // or .AddPostgresEventStore()

// Use the event store
public class OrderService
{
    private readonly IEventStore _eventStore;

    public OrderService(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async Task CreateOrder(CreateOrderCommand command)
    {
        var events = new[]
        {
            new OrderCreated(command.OrderId, command.CustomerId),
            new OrderItemAdded(command.OrderId, command.ProductId, command.Quantity)
        };

        var query = new StreamQuery()
            .WithTags(new EventTag("order", command.OrderId.ToString()));

        await _eventStore.AppendAsync(query, events);
    }
}
```

## Features

- **IEventStore** - Main event store interface
- **IMultiTenantEventStore** - Multi-tenant support
- **StreamQuery** - Flexible event querying
- **Event Subscriptions** - Real-time event notifications
- **Serialization** - JSON-based event serialization

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
