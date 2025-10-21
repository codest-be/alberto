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

### Module-Based Configuration (Recommended)

```csharp
using Alberto.EventStore;

// Define your EventStore type
public class OrderEventStore : EventStoreFactory { }

// Configure the module
services.AddModule<OrderEventStore>("orders", module => module
    .WithInMemory()  // or .WithPostgres(options => { ... })
    .WithMultiTenancy<MyTenantContext>());  // Optional

// Use in your services
public class OrderService
{
    private readonly OrderEventStore _eventStore;

    public OrderService(OrderEventStore eventStore)
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

        await _eventStore.Append(events, query, null);
    }
}
```

### Legacy Configuration

```csharp
// Legacy approach (still supported)
services.AddEventStore()
        .AddInMemoryEventStore();

// Inject IEventStore instead of typed EventStore
public OrderService(IEventStore eventStore) { }
```

## Features

- **ModuleBuilder** - Fluent API for module configuration
- **EventStoreFactory** - Base class for typed event stores
- **StreamQuery** - Flexible event querying with builder pattern
- **Channel Subscriptions** - Ultra-low latency in-process pub/sub
- **Polling Subscriptions** - Traditional pull-based subscriptions
- **Multi-tenancy** - Isolated event streams per tenant
- **Serialization** - JSON-based event serialization
- **Telemetry** - OpenTelemetry integration support

## Module-Based Architecture

Alberto uses a module-based architecture where each EventStore is a unique type:

```csharp
public class OrderEventStore : EventStoreFactory { }
public class PaymentEventStore : EventStoreFactory { }

// Each gets independent configuration
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => options.Schema = "orders"));

services.AddModule<PaymentEventStore>("payments", module => module
    .WithPostgres(options => options.Schema = "payments"));
```

**Benefits:**

- Multiple event stores with different backends
- Schema isolation per module
- Type-safe service resolution
- Independent subscription configuration

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
