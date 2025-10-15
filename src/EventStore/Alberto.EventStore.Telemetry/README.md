# Alberto.EventStore.Telemetry

OpenTelemetry integration for Alberto EventStore.

## Installation

```bash
dotnet add package Alberto.EventStore.Telemetry
```

## Overview

This package provides OpenTelemetry instrumentation for Alberto EventStore, enabling:

- Distributed tracing for event store operations
- Activity tracking for append, load, and query operations
- Trace context propagation across services
- Integration with observability platforms (Jaeger, Zipkin, Azure Monitor, etc.)

## Quick Start

```csharp
using Alberto.EventStore;
using Alberto.EventStore.Telemetry;

// Configure services
services.AddEventStore()
        .AddInMemoryEventStore() // or .AddPostgresEventStore()
        .AddTelemetry(); // Add telemetry support

// Configure OpenTelemetry
services.AddOpenTelemetry()
        .WithTracing(builder =>
        {
            builder.AddSource("Alberto.EventStore");
            builder.AddConsoleExporter(); // or other exporters
        });
```

## Features

- **Activity Tracing** - Automatic activity creation for event store operations
- **Trace Context** - Propagates trace context with events
- **Custom Tags** - Enriches traces with event store metadata
- **Diagnostic Events** - Listens to EventStore diagnostic events

## Traced Operations

The following operations are automatically instrumented:

- `EventStore.Append` - Event append operations
- `EventStore.Load` - Event load operations
- `EventStore.Subscribe` - Subscription operations

## Integration with Observability Platforms

Works with any OpenTelemetry-compatible platform:

- Jaeger
- Zipkin
- Azure Monitor / Application Insights
- AWS X-Ray
- Datadog
- Honeycomb
- And more...

## Documentation

For more information, see the [main repository README](https://github.com/codest-be/alberto).
