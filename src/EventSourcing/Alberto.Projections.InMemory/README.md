# Alberto.Projections.InMemory

In-memory implementation of Alberto projection repositories for testing and development.

## Installation

```bash
dotnet add package Alberto.Projections.InMemory
```

## Overview

This package provides an in-memory backend for Alberto projections, ideal for:

- Unit testing
- Integration testing
- Local development
- Prototyping

## Quick Start

```csharp
using Alberto.Projections.InMemory;

// Configure services
services.AddInMemoryProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>();
```

## Usage

```csharp
public record OrderSummary
{
    public Guid Id { get; init; }
    public decimal Total { get; init; }
    public string Status { get; init; } = string.Empty;
}

public class OrderSummaryProjector : IProjector<OrderSummary>
{
    public OrderSummary Apply(OrderSummary state, object @event)
    {
        return @event switch
        {
            OrderCreated created => state with { Id = created.OrderId },
            OrderTotalCalculated calc => state with { Total = calc.Total },
            OrderCompleted => state with { Status = "Completed" },
            _ => state
        };
    }
}
```

## Features

- Fast, in-memory projection storage
- Full projection repository API support
- No external dependencies
- Ideal for testing scenarios
- Thread-safe operations

## Note

⚠️ **This implementation is NOT suitable for production use.** All data is stored in memory and will be lost when the
application restarts. For production scenarios, use `Alberto.Projections.EfCore` with your preferred database provider (
SQL Server, PostgreSQL, MySQL, etc.).

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
