# Alberto Platform Snapshot

- **What it is**: Modular .NET event sourcing stack with an EventStore abstraction (in-memory and PostgreSQL backends), projection infrastructure, optional CQRS layer, telemetry, and reference example. Targets multi-tenant, multi-schema deployments with pluggable subscriptions.
- **Packages**: Core EventStore + Postgres/InMemory backends and telemetry, EventSourcing primitives and projection repositories, CQRS and CQRS telemetry, testing helpers (component + unit spec harnesses), and migration script generator tooling.
- **Example**: Orders/Payments bounded contexts showing hybrid subscriptions, PostgreSQL schemas per module, multi-tenant middleware, and Aspire orchestration (AppHost + ServiceDefaults).
- **Standards**: ModuleBuilder for typed stores, keyed DI to isolate modules, JSONB storage with GIN indexes and optional COPY bulk insert, channel subscriptions (sync/async/hybrid), optimistic concurrency via consistency boundaries, OpenTelemetry tracing/metrics baked in.
- **Focus now**: Postgres path for production (schema-per-module, pooling tuned), with in-memory paths kept for tests and fast starts.
