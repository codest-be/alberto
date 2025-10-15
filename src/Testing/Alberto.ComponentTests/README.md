# Alberto.ComponentTests

Testing utilities for component-level testing with Alberto EventStore.

## Installation

```bash
dotnet add package Alberto.ComponentTests
```

## Overview

This package provides testing utilities for writing component tests with Alberto EventStore:

- **ScenarioContext** - Tracks events and state during test scenarios
- **UseCase** - Base class for defining reusable test use cases
- **IServiceFixture** - Interface for test service fixtures
- **SubscriptionEventCollector** - Collects events from subscriptions for verification

## Quick Start

```csharp
using Alberto.ComponentTests;

public class OrderTests : IClassFixture<ServiceFixture>
{
    private readonly ServiceFixture _fixture;

    public OrderTests(ServiceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Can_create_order()
    {
        // Arrange
        var context = new ScenarioContext();
        var command = new CreateOrderCommand { Amount = 100 };

        // Act
        await _fixture.ExecuteAsync(context, command);

        // Assert
        context.ShouldHaveEvent<OrderCreated>();
    }
}
```

## Features

- **ScenarioContext** - Contextual test state management
- **Event Collection** - Automatic event capture during tests
- **Extension Methods** - Fluent assertions for events
- **Service Fixtures** - Reusable test infrastructure setup

## Use Cases

This package is designed for testing:

- Event-driven workflows
- CQRS command handlers
- Event subscriptions
- Integration between components

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
