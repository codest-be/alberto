# Alberto Projections - PostgreSQL Implementation

PostgreSQL implementation of Alberto projection repositories with lightweight schema migrations.

## Features

- JSONB-based projection storage for flexible schemas
- Automatic projection table creation per state type
- Schema-level isolation for multi-tenant or multi-context scenarios
- Optimistic updates with global version tracking

## Schema Migrations

### Automatic Migrations (Development/Testing)

Enable automatic migrations by setting `RunMigrations = true`. Migrations run via `IHostedService` at application
startup, before the app accepts requests:

```csharp
services.AddPostgresProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>(options =>
{
    options.ConnectionString = "Host=localhost;Database=mydb;Username=user;Password=pass";
    options.Schema = "orders";  // REQUIRED: Schema must be specified
    options.RunMigrations = true;  // Opt-in for automatic migrations at startup
});
```

**How it works:**

- When `RunMigrations = true`, the schema is registered for migration
- `ProjectionMigrationHostedService` runs at application startup
- All registered schemas are migrated before accepting requests
- Multiple schemas are deduplicated automatically
- Fast and non-blocking DI container setup

### Manual Migrations (Production - Recommended)

For production environments, manage migrations separately:

```csharp
services.AddPostgresProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "orders";  // REQUIRED: Schema must be specified
    options.RunMigrations = false;  // Default: migrations disabled
});

// Run migrations manually (e.g., during deployment)
var migrationRunner = new ProjectionMigrationRunner(connectionString, logger);
var success = await migrationRunner.MigrateAsync("orders");
```

### Custom Migration Tools

Implement `IProjectionMigrationRunner` to use your own migration framework:

```csharp
public class CustomMigrationRunner : IProjectionMigrationRunner
{
    public async Task<bool> MigrateAsync(string schema, CancellationToken cancellationToken = default)
    {
        // Your custom migration logic (e.g., DbUp, FluentMigrator, etc.)
        return true;
    }
}
```

## How It Works

1. **Schema Creation**: When `RunMigrations = true`, the specified schema is created if it doesn't exist
2. **Table Creation**: Each projection state type gets its own table (e.g., `ordersummary_projections`)
3. **Per-Projection Isolation**: Tables are created automatically on first use within the schema

## Multiple Schemas

You can use different schemas for different projection contexts:

```csharp
// Order projections in "orders" schema
services.AddPostgresProjectionRepository<Guid, OrderSummary, OrderSummaryProjector>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "orders";
    options.RunMigrations = true;
});

// Inventory projections in "inventory" schema
services.AddPostgresProjectionRepository<Guid, InventoryView, InventoryProjector>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "inventory";
    options.RunMigrations = true;
});
```

## Projection Table Structure

Each projection table is automatically created with:

- `tenant_id` - Multi-tenant support
- `key` - Projection key (composite primary key with tenant_id)
- `state` - JSONB column storing the projected state
- `global_version` - Version tracking for idempotent updates
- `updated_at` - Timestamp of last update

## Best Practices

1. **Always specify a schema** - Required for proper isolation
2. **Use `RunMigrations = false` in production** - Run migrations via deployment pipeline
3. **One schema per bounded context** - Separate schemas for different domains
4. **Version your projections** - Use `UpdateWithVersion` for idempotent event replay

