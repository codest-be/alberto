# Alberto.AppHost

.NET Aspire host that orchestrates the example API, PostgreSQL, pgAdmin, and load tests.

- **Services**: Postgres server with persistent volume and tuned settings (`max_connections=200`, `pg_stat_statements`),
  database `alberto-db`, example API with 3 replicas, pgAdmin, and k6-based load tests wired to the API endpoint.
- **Run**:
  ```bash
  dotnet run --project src/Example/Alberto.AppHost
  ```
  Aspire injects the `alberto-db` connection string into the API and keeps dependencies healthy before startup.
- **Notes**: Load tests (`tst/Alberto.Example.LoadTests`) are configured but marked `ExplicitStart`; trigger from Aspire
  dashboard or CLI when ready.
