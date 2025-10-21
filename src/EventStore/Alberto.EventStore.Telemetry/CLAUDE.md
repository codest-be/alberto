# Alberto.EventStore.Telemetry - OpenTelemetry Integration

This document explains the telemetry integration for Alberto EventStore using OpenTelemetry.

## Overview

Alberto.EventStore.Telemetry provides automatic distributed tracing for event store operations using OpenTelemetry
Activities. It enables observability across event-driven workflows with minimal configuration.

## Key Design Decisions

### 1. Activity-Based Tracing

Uses `System.Diagnostics.Activity` for distributed tracing:

```csharp
public class ActivityDiagnosticsEventListener : IDiagnosticsEventListener
{
    private static readonly ActivitySource ActivitySource =
        new("Alberto.EventStore", "1.0.0");

    public IDisposable Append(IEnumerable<IEventToPersist> events)
    {
        var activity = ActivitySource.StartActivity("EventStore.Append");
        activity?.SetTag("event.count", events.Count());
        return new AppendScope(activity);
    }
}
```

**Why Activities?**

- Native .NET distributed tracing
- Automatic context propagation
- Compatible with all OpenTelemetry exporters
- Zero overhead when not enabled

### 2. Trace Context Propagation

Telemetry metadata is embedded in event metadata for cross-service tracing:

```csharp
public Dictionary<string, string> GetTelemetryMetadata()
{
    var metadata = new Dictionary<string, string>();
    var current = Activity.Current;

    if (current != null)
    {
        metadata["TraceId"] = current.TraceId.ToString();
        metadata["SpanId"] = current.SpanId.ToString();
        metadata["ParentSpanId"] = current.ParentSpanId.ToString();
    }

    return metadata;
}
```

**Workflow:**

1. Start activity for append operation
2. Extract trace context (TraceId, SpanId)
3. Embed in event metadata
4. Downstream consumers can continue the trace

### 3. Scoped Diagnostics

Different scopes for different operations:

- **AppendScope**: Tracks event append operations
    - Tags: event count, tenant ID, event types
    - Duration: Time to persist events

- **StreamScope**: Tracks query operations
    - Tags: query filters, max count, result count
    - Duration: Time to query events

### 4. Diagnostic Scopes with IDisposable

Scopes implement `IDisposable` for automatic activity cleanup:

```csharp
public class AppendScope : IDisposable
{
    private readonly Activity? _activity;

    public AppendScope(Activity? activity) => _activity = activity;

    public void Dispose()
    {
        _activity?.Stop();
        _activity?.Dispose();
    }
}
```

**Benefits:**

- Automatic activity lifecycle management
- `using` statement ensures cleanup
- No manual Start/Stop calls

### 5. Integration via ModuleBuilder

Enable telemetry with `.WithTelemetry()`:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => { ... })
    .WithTelemetry());  // Enables OpenTelemetry
```

**What it registers:**

- `IDiagnosticsEventListener` → `ActivityDiagnosticsEventListener`
- `ITraceContextProvider` → `ActivityTraceContextProvider`
- Replaces no-op implementations

## Configuration

### Basic Setup

```csharp
// 1. Add telemetry to EventStore module
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => { ... })
    .WithTelemetry());

// 2. Configure OpenTelemetry
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("Alberto.EventStore")  // EventStore activities
        .AddSource("Alberto.CQRS")        // CQRS activities (optional)
        .AddConsoleExporter());
```

### Production Setup with Azure Monitor

```csharp
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("Alberto.EventStore")
        .AddSource("Alberto.CQRS")
        .AddAzureMonitorTraceExporter(options =>
        {
            options.ConnectionString = configuration["ApplicationInsights:ConnectionString"];
        }));
```

### Activity Tags

EventStore activities include rich metadata:

```csharp
// Append operation
activity.SetTag("event.count", eventCount);
activity.SetTag("tenant.id", tenantId);
activity.SetTag("event.types", string.Join(",", eventTypes));

// Stream operation
activity.SetTag("query.has_filters", hasFilters);
activity.SetTag("query.max_count", maxCount);
activity.SetTag("result.count", resultCount);
```

## Trace Context in Subscriptions

Subscriptions automatically restore trace context from event metadata:

```csharp
public class TelemetryConsumeFilter : IConsumeFilter
{
    public async Task<ConsumeFilterResult> Execute(
        EventContext context,
        ConsumeFilterDelegate next)
    {
        // Extract trace context from event metadata
        var traceId = context.Event.Metadata.GetValueOrDefault("TraceId");
        var spanId = context.Event.Metadata.GetValueOrDefault("SpanId");

        // Create new activity linked to original trace
        using var activity = ActivitySource.StartActivity(
            "EventStore.Consume",
            ActivityKind.Consumer,
            new ActivityContext(traceId, spanId, ...));

        return await next();
    }
}
```

**Result:**

- End-to-end tracing across async operations
- See full workflow: Command → Append → Subscription → Handler

## Performance Considerations

### Zero Overhead When Disabled

If OpenTelemetry is not configured, activities are not created:

```csharp
var activity = ActivitySource.StartActivity("EventStore.Append");
// Returns null if no listeners are registered
if (activity == null) return EmptyScope.Instance;
```

### Minimal Overhead When Enabled

- Activity creation: <1μs
- Tag setting: <0.1μs per tag
- Total overhead: ~2-5μs per operation

## Common Pitfalls

1. **Forgetting to add activity source**: Must add `.AddSource("Alberto.EventStore")` to OpenTelemetry
2. **Not linking traces in subscriptions**: Use `TelemetryConsumeFilter` (auto-registered)
3. **Over-tagging**: Too many tags adds overhead and storage cost

## File Structure

- `ActivityDiagnosticsEventListener.cs` - Main diagnostics implementation
- `ActivityTraceContextProvider.cs` - Trace context extraction
- `Scopes/AppendScope.cs` - Append operation scope
- `Scopes/StreamScope.cs` - Query operation scope
- `Scopes/EmptyScope.cs` - No-op scope when telemetry disabled
- `ModuleBuilderExtensions.cs` - `.WithTelemetry()` extension
- `Tags.cs` - Standardized tag names

## Integration with APM Tools

### Application Insights (Azure)

```csharp
.AddAzureMonitorTraceExporter()
```

### Jaeger

```csharp
.AddJaegerExporter()
```

### Zipkin

```csharp
.AddZipkinExporter()
```

### DataDog

```csharp
.AddOtlpExporter(options => options.Endpoint = "https://api.datadoghq.com")
```

All standard OpenTelemetry exporters are supported.
