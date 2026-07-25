# Alberto

Alberto is a DCB (Dynamic Consistency Boundary) event store for .NET. It gives you an
append-only event log with configurable consistency boundaries, background async event
processing, and a declarative configuration API designed to make misconfiguration
a compile-time or startup-time error.

## Install

```sh
dotnet add package Alberto.Dcb
dotnet add package Alberto.Dcb.Postgres       # PostgreSQL production backend
dotnet add package Alberto.Dcb.InMemory       # in-memory backend (tests, local dev)
dotnet add package Alberto.Dcb.Telemetry      # optional: OpenTelemetry tracing + metrics
dotnet add package Alberto.Dcb.EntityFramework # optional: EF Core projection helpers
```

Projects multi-target `net9.0` and `net10.0`.

## Minimal example

Register a module in your DI composition root — `Program.cs` or an `IServiceCollection`
extension:

```csharp
using Alberto.Dcb;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Telemetry;
using Microsoft.Extensions.DependencyInjection;

services.AddAlberto("orders", module => module
    .WithPostgres(o => o with
    {
        ConnectionString = connectionString,
        Schema = "orders",
    })
    .WithTelemetry()
    .WithControlLoop(o => o with { BatchSize = 500 }));
```

Nothing is registered yet when `AddAlberto` returns — declaration, configuration overlay,
validation, and service registration happen in three distinct phases before the host starts.
See [docs/configuration.md](docs/configuration.md) for the full picture.

## Configuration

Alberto reads every knob from `Alberto:Modules:{moduleKey}:{Section}:{Property}` in
`appsettings.json` (or any other `IConfiguration` source). A value supplied this way
**wins over the code default** — you can change batch sizes or retry counts in any
environment without redeploying:

```json
{
  "Alberto": {
    "Modules": {
      "orders": {
        "ControlLoop": {
          "PollingInterval": "00:00:00.100",
          "BatchSize": 1000
        },
        "Checkpoints": {
          "OrphanPolicy": "Strict"
        }
      }
    }
  }
}
```

→ [docs/configuration.md](docs/configuration.md) — full option table, precedence rules,
  validation catalog, and custom backend guide.

## The Orders module

The canonical example lives in
`apps/Alberto.Orders/Alberto.Orders.Infrastructure/OrdersModule.cs`. It shows the
complete declaration: tenancy, Postgres backend, Entity Framework projections, telemetry,
and a custom control-loop interval.

## Configuration reference and upgrade guide

- [docs/configuration.md](docs/configuration.md) — three-phase pipeline, all options,
  validation codes, custom backends
- [UPGRADING.md](UPGRADING.md) — what changed between 0.x and 1.0, and what to do about
  each change
