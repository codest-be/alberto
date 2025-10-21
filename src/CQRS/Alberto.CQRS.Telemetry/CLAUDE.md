# Alberto.CQRS.Telemetry - CQRS Telemetry Integration

## Overview

Provides OpenTelemetry tracing for CQRS command and query execution with automatic activity creation and tagging.

## Key Features

### 1. Command and Query Activities

Creates activities for command/query execution:

```csharp
public ICommandScope Command(Type commandType, string commandName, string? moduleKey, bool hasReturnValue)
{
    var activity = ActivitySource.StartActivity("CQRS.Command");
    activity?.SetTag("command.type", commandType.FullName);
    activity?.SetTag("command.name", commandName);
    activity?.SetTag("module.key", moduleKey);
    activity?.SetTag("has.return.value", hasReturnValue);

    return new CommandScope(activity);
}
```

### 2. Validation Tracking

Tracks validation failures:

```csharp
scope.WithValidationFailure(new[] { "INVALID_AMOUNT", "INVALID_CUSTOMER" });
// Sets tags: validation.failed = true, validation.errors = "INVALID_AMOUNT,INVALID_CUSTOMER"
```

### 3. Handler Tracking

Tracks handler type and execution outcome:

```csharp
scope.WithHandler(typeof(CreateOrderHandler));
scope.WithOutcome("success");  // or "failure"
scope.WithError("Error message", new[] { "ERROR_CODE" });
```

## Configuration

```csharp
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("Alberto.CQRS")
        .AddConsoleExporter());
```

## Activity Structure

```
CQRS.Command (CreateOrderCommand)
├─ validation.failed: false
├─ handler.type: CreateOrderHandler
├─ outcome: success
└─ duration: 15ms
```

## File Structure

- `ActivityDiagnosticEventListener.cs` - Main listener
- `Scopes/CommandScope.cs` - Command activity scope
- `Scopes/QueryScope.cs` - Query activity scope
- `AlbertoCQRSActivitySource.cs` - Activity source definition
- `ServiceCollectionExtensions.cs` - DI registration

## Integration

Automatically integrates when using `AddCqrsModule()` or `.WithCQRS()`.
