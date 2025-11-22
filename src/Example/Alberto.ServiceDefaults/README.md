# Alberto.ServiceDefaults

Shared Aspire defaults for services in the solution: health checks, service discovery, resilience, and OpenTelemetry
wiring.

- **What it adds**: Health endpoints (`/health`, `/alive` in Development), default HTTP client resilience + discovery,
  Npgsql instrumentation, Alberto EventStore + CQRS telemetry, and OTLP exporter hookup via
  `OTEL_EXPORTER_OTLP_ENDPOINT`.
- **Use**: Call `builder.AddServiceDefaults()` in your `WebApplicationBuilder` or `IHostApplicationBuilder` to apply the
  defaults, then `app.MapDefaultEndpoints()` to expose health probes.
- **Tracing filter**: `FilteringProcessor` drops standalone Npgsql activities (keeps DB spans nested under app spans) to
  reduce noise.
- **Configuration**: Honors standard logging settings and can layer additional exporters (e.g., Azure Monitor) by
  uncommenting the provided stubs.
