# Alberto EventStore - PostgreSQL Implementation

PostgreSQL implementation of Alberto EventStore with lightweight schema migrations.

## Features

- High-performance event storage with multi-schema support
- Optimistic concurrency control
- Automatic or manual schema migrations (idempotent SQL scripts)
- Per-schema isolation (multiple event stores in different schemas)

## Schema Migrations

### Automatic Migrations (Development/Testing)

Enable automatic migrations by setting `RunMigrations = true`. Migrations run via `IHostedService` at application
startup, before the app accepts requests:

```csharp
services.AddPostgresEventStore<MyEventStoreFactory>(options =>
{
    options.ConnectionString = "Host=localhost;Database=mydb;Username=user;Password=pass";
    options.Schema = "orders";  // REQUIRED: Schema must be specified
    options.RunMigrations = true;  // Opt-in for automatic migrations at startup
});
```

**How it works:**

- When `RunMigrations = true`, the schema is registered for migration
- `EventStoreMigrationHostedService` runs at application startup
- All registered schemas are migrated before accepting requests
- Multiple schemas are deduplicated automatically
- Fast and non-blocking DI container setup

### Manual Migrations (Production - Recommended)

For production environments, manage migrations separately:

```csharp
services.AddPostgresEventStore<MyEventStoreFactory>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "orders";  // REQUIRED: Schema must be specified
    options.RunMigrations = false;  // Default: migrations disabled
});

// Run migrations manually (e.g., during deployment)
var migrationRunner = new EventStoreMigrationRunner(connectionString, logger);
var success = await migrationRunner.MigrateAsync("orders");
```

### Custom Migration Tools

Implement `IEventStoreMigrationRunner` to use your own migration framework:

```csharp
public class CustomMigrationRunner : IEventStoreMigrationRunner
{
    public async Task<bool> MigrateAsync(string schema, CancellationToken cancellationToken = default)
    {
        // Your custom migration logic (e.g., DbUp, FluentMigrator, etc.)
        return true;
    }
}
```

## Multiple Schemas

You can create multiple event stores in different schemas:

```csharp
// Orders event store in "orders" schema
services.AddPostgresEventStore<OrderEventStoreFactory>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "orders";
    options.RunMigrations = true;
});

// Inventory event store in "inventory" schema
services.AddPostgresEventStore<InventoryEventStoreFactory>(options =>
{
    options.ConnectionString = connectionString;
    options.Schema = "inventory";
    options.RunMigrations = true;
});
```

## Migration Scripts

Migration scripts are embedded SQL resources in the `Migrations` folder:

- `001_CreateEventStoreSchema.sql` - Initial schema creation with events, checkpoints, and poison pill tables

**Important**: All migration scripts must be **idempotent** (use `CREATE IF NOT EXISTS`, etc.). Scripts are executed in
alphabetical order by filename.

## Best Practices

1. **Always specify a schema** - Required for proper isolation
2. **Use `RunMigrations = false` in production** - Run migrations via deployment pipeline
3. **One schema per bounded context** - Separate schemas for different domains
4. **Test migrations** - Always test migration scripts before production deployment
