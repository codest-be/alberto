# Alberto.CQRS.Telemetry

OpenTelemetry instrumentation for Alberto.CQRS command/query pipelines.

- **What it does**: Creates activities for commands and queries, tags validation outcomes, handler types, module keys,
  and errors, and enriches traces with Alberto-specific tags.
- **Install**: `dotnet add package Alberto.CQRS.Telemetry`
- **Configure**:
  ```csharp
  services.AddCqrsModule()
      .WithTelemetry();                  // enables ActivityDiagnosticEventListener

  services.AddOpenTelemetry()
      .WithTracing(builder => builder
          .AddSource("Alberto.CQRS")     // emitted by AlbertoCQRSActivitySource
          .AddAspNetCoreInstrumentation()
          .AddHttpClientInstrumentation());
  ```
- **Tags emitted**: `command.type`, `command.name`, `query.type`, `validation.failed`, `validation.errors`,
  `handler.type`, `module.key`, `outcome`, `error.message`, `error.codes`, plus duration/trace ids from the ambient
  activity.
- **Use when**: You need end-to-end traces across CQRS handlers (especially alongside `Alberto.EventStore.Telemetry`) to
  correlate append/load operations with command execution.
