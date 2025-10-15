# Alberto.UnitTests

Testing utilities for unit-level testing with Alberto EventStore.

## Installation

```bash
dotnet add package Alberto.UnitTests
```

## Overview

This package provides testing utilities for writing unit tests with Alberto EventStore:

- **Specification** - Base class for specification-style unit tests
- **SpecificationBase** - Generic base for given-when-then tests
- **StatelessSpecification** - Specification pattern for stateless scenarios

## Quick Start

```csharp
using Alberto.UnitTests;

public class CreateOrderTests : Specification<OrderState, CreateOrderCommand, OrderId>
{
    protected override OrderState Given()
    {
        return new OrderState(); // Initial state
    }

    protected override CreateOrderCommand When()
    {
        return new CreateOrderCommand
        {
            CustomerId = "customer-123",
            Amount = 100
        };
    }

    [Fact]
    public void Should_create_order_with_correct_amount()
    {
        var result = Execute();
        
        result.Should().BeSuccess();
        result.Value.Should().NotBeNull();
    }
}
```

## Features

- **Specification Pattern** - Given-When-Then test structure
- **Stateful Tests** - Test with state evolution
- **Stateless Tests** - Test pure business logic
- **Fluent API** - Clear and expressive test code

## Testing Patterns

### Specification Pattern

The specification pattern helps organize tests with a clear structure:

- **Given** - Initial state
- **When** - Action to perform
- **Then** - Assertions (in test methods)

### Stateless Specifications

For testing pure functions without state:

```csharp
public class CalculateTotalTests : StatelessSpecification<CalculateTotalCommand, decimal>
{
    protected override CalculateTotalCommand When()
    {
        return new CalculateTotalCommand { Items = [...] };
    }

    [Fact]
    public void Should_calculate_correct_total()
    {
        var result = Execute();
        result.Should().Be(150);
    }
}
```

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
