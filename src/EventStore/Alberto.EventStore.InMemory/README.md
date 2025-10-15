# Alberto.EventStore.InMemory

In-memory implementation of Alberto EventStore for testing and development.

## Installation

```bash
dotnet add package Alberto.EventStore.InMemory
```

## Overview

This package provides an in-memory backend for Alberto EventStore, ideal for:

- Unit testing
- Integration testing
- Local development
- Prototyping

## Quick Start

```csharp
using Alberto.EventStore;
using Alberto.EventStore.InMemory;

// Configure services
services.AddEventStore()
        .AddInMemoryEventStore();
```

## Features

- Fast, in-memory event storage
- Full event store API support
- No external dependencies
- Ideal for testing scenarios

## Note

⚠️ **This implementation is NOT suitable for production use.** All data is stored in memory and will be lost when the application restarts. Use `Alberto.EventStore.Postgres` for production scenarios.

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
