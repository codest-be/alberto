# Alberto.Example

Reference API that demonstrates Alberto’s Orders and Payments bounded contexts using PostgreSQL-backed event stores,
projections, and CQRS.

- **What it provides**: Minimal API endpoints for order/payment lifecycles, hybrid subscriptions (sync projections +
  async stats), and multi-tenant middleware (`X-Tenant-Id` header, defaults to `default`).
- **Data store**: Each module uses its own schema (`orders`, `payments`) over a shared Postgres database. Connection
  string key: `alberto-db` (injected by Aspire or appsettings).
- **Run locally**:
  ```bash
  dotnet run --project src/Example/Alberto.Example
  # Swagger UI available in Development at /
  ```
- **Endpoints**: `/api/orders` (create/place/ship/cancel/get), `/api/order-statistics`, `/api/payments` (
  create/pay/get).
- **Observability**: Adds OpenTelemetry via `Alberto.ServiceDefaults`, including Alberto EventStore + CQRS
  instrumentation and PostgreSQL client traces.
- **Migrations**: In Development, migrations are added via `AddDatabaseMigrations()` (EventStore + projection schemas);
  for production use the migration script generator.
