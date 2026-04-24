# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

Alberto is a DCB (Dynamic Consistency Boundary) Event Store system with a .NET 10.0 backend and Angular 21.1.0 admin dashboard. The repository is a monorepo containing core libraries, applications, and tests.

## Build & Run Commands

### Full Stack (Recommended)
```bash
# Start all services via .NET Aspire (PostgreSQL, Orders API, Admin Web, K6)
dotnet run --project apps/Alberto.AppHost
```
This starts:
- PostgreSQL on port 5432 (with PgAdmin on 8080)
- Orders API on port 5180
- Angular Admin Web on port 4200

### .NET Backend
```bash
dotnet build                    # Build all projects
dotnet test                     # Run all xUnit tests
dotnet test --filter "FullyQualifiedName~TestName"  # Run single test
```

### Angular Frontend
```bash
cd apps/Alberto.Admin.Web
npm start                       # Dev server on port 4200 (proxies to backend)
npm run build                   # Production build
npm test                        # Run Vitest tests
```

### Load Tests
```bash
cd tests/Alberto.Orders.LoadTests
npm install && npm run build
npm run test:smoke              # Quick validation (30s)
npm run test:load               # Full load test (~14min)
```

## Architecture

### Directory Structure
```
/apps/                          # Applications
  Alberto.AppHost/              # Aspire orchestration (runs everything)
  Alberto.Admin.Web/            # Angular 21 admin SPA
  Alberto.Orders/               # Orders microservice
    Alberto.Orders.Api/         # GraphQL API
    Alberto.Orders.Core/        # Domain models
    Alberto.Orders.Infrastructure/  # Data access

/src/                           # Core libraries (packable NuGet)
  Alberto.Dcb/                  # Event store abstractions
  Alberto.Dcb.Admin/            # Admin REST/GraphQL endpoints
  Alberto.Dcb.Postgres/         # PostgreSQL backend
  Alberto.Dcb.InMemory/         # In-memory backend (dev/test)
  Alberto.Dcb.Telemetry/        # OpenTelemetry instrumentation

/tests/                         # Test projects
  Alberto.Dcb.Tests/            # Unit tests (xUnit 3)
  Alberto.Orders.LoadTests/     # K6 load tests (TypeScript)
```

### Key Patterns
- **Event Sourcing with DCB**: Append-only event log with dynamic consistency boundaries
- **Async Processing**: PollingConsumer routes events to projections/reactors. See [docs/architecture/async-processing.md](docs/architecture/async-processing.md)
- **Multi-Tenant**: X-Tenant-Id header propagation, tenant-isolated queries
- **GraphQL**: HotChocolate 15.x with real-time subscriptions via WebSockets
- **Angular Signals**: Components use `signal()`, `computed()`, `takeUntilDestroyed()`
- **Zoneless**: Angular 21 zoneless change detection enabled

### Angular Frontend Structure
```
src/app/
├── core/                       # Services, GraphQL client, models
├── features/                   # Lazy-loaded feature modules
│   ├── dashboard/
│   ├── processors/
│   ├── checkpoints/
│   ├── dead-letters/
│   └── projections/
└── shared/                     # Reusable components
```

### Backend Services
- **AdminApiService**: REST endpoints for processor/checkpoint management
- **AdminSubscriptionService**: GraphQL subscriptions for real-time updates
- **PostgresAdminDataAccess**: Direct PostgreSQL queries for admin data

## Technology Stack

| Layer | Technology |
|-------|------------|
| Backend Framework | .NET 10.0, ASP.NET Core |
| GraphQL | HotChocolate 15.1.12 |
| Database | PostgreSQL 15+ (Npgsql 10.0.1) |
| Migrations | DbUp-PostgreSQL |
| Frontend | Angular 21.1.0 (standalone components) |
| GraphQL Client | Apollo Angular 13.0.0 |
| Observability | OpenTelemetry 1.14.0 |
| Testing | xUnit 3.2.2, FluentAssertions, Testcontainers |
| Load Testing | K6 with TypeScript |

## Package Management

- **NuGet**: Centralized versions in `Directory.Packages.props`
- **npm**: Per-project `package.json` files

## Configuration

- **Solution file**: `AlbertoV3.slnx` (modern .NET format)
- **Build settings**: `Directory.Build.props`
- **Angular proxy**: `apps/Alberto.Admin.Web/proxy.conf.js` (proxies `/graphql` and `/alberto` to backend)
