# Architecture: Migrations & Projections

## ✅ IMPLEMENTED: Migration Strategy System

Alberto now uses a **pluggable migration strategy** system that gives users full control over when and how EventStore schema migrations are applied, while the library maintains the schema definitions.

## Current Architecture (v1.0)

### **Principle: EventStore ≠ Projections**

The EventStore schema and projection schemas are **completely separate concerns**:

- **EventStore schema**: Managed by Alberto via pluggable `IMigrationStrategy`
- **Projection schema**: Managed by YOU (EF Core, Dapper, plain SQL, whatever)

---

## 1. ✅ Pluggable Migration Strategies (Implemented)

### How It Works

Alberto ships with **embedded migration templates** (SQL files with `{schema}` placeholder) that define the EventStore schema structure. On first run, these templates are generated to your project at `./Migrations/EventStore/{schema}/`, allowing you to review and modify them before application.

```csharp
public interface IMigrationStrategy
{
    Task EnsureSchemaAsync(string schema, string connectionString, CancellationToken ct);
}
```

### Implementation Details

**Migration Templates (embedded in library):**
```
Alberto.EventStore.Postgres/Migrations/Templates/
└── 001_InitialSchema.sql (events, checkpoints, poison_pills, __alberto_schema_version)
```

**Generated Files (in your project):**
```
YourProject/Migrations/EventStore/
├── orders/
│   └── 001_InitialSchema.sql
└── payments/
    └── 001_InitialSchema.sql
```

**Idempotent Migrations:**
Each migration checks `{schema}.__alberto_schema_version` before applying, making them safe to run multiple times.

### Usage Patterns

#### **Option 1: AutoMigration (Development - Default)**
```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
        // Default: AutoMigrationStrategy
        // - Generates ./Migrations/EventStore/orders/*.sql on first run
        // - Applies migrations automatically
        // - Idempotent (safe to re-run)
    }));
```

#### **Option 2: NoMigration (Production)**
```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
        options.MigrationStrategy = new NoMigrationStrategy();  // ✅ No auto-migrations
    }));
```

You manage migrations externally via your deployment pipeline:
```bash
# Apply migrations manually
psql -f ./Migrations/EventStore/orders/001_InitialSchema.sql
```

#### **Option 3: ScriptOnly (CI/CD)**
```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
        options.MigrationStrategy = new ScriptOnlyMigrationStrategy("./Migrations");
    }));
```

Generates SQL scripts without executing them:
```
./Migrations/EventStore/orders/001_InitialSchema.sql
./Migrations/EventStore/payments/001_InitialSchema.sql
```

Review → commit to source control → deploy via Flyway/Liquibase/DbUp.

#### **Option 4: Environment-Based (Recommended)**
```csharp
#if DEBUG
    options.MigrationStrategy = new AutoMigrationStrategy();  // ⚠️ Dev: auto-apply
#else
    options.MigrationStrategy = new NoMigrationStrategy();    // ✅ Prod: manual control
#endif
```

---

## 2. ✅ Projections Decoupled from EventStore (Implemented)

### What Changed

Alberto no longer provides `AddPostgresProjection` or automatic projection table creation. **Projections are now just subscription handlers** - you choose how to store your read models.

### Why This Is Better

- ✅ Use EF Core with navigation properties and LINQ
- ✅ Use Dapper with custom SQL
- ✅ Use any database (MongoDB, Redis, Elasticsearch, etc.)
- ✅ Full control over schema, indexes, and migrations
- ✅ Clear separation: EventStore schema vs Projection schema

### Recommended Approach

**See [EF_CORE_INTEGRATION.md](examples/EF_CORE_INTEGRATION.md) for a complete guide.**

#### **Example: EF Core Projections**

```csharp
// Your EF Core DbContext
public class OrderDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderLine> OrderLines { get; set; }

    // Full control over schema
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders", "read_models");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.CustomerId).IsRequired();
            entity.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });
    }
}

// Subscription handler using EF Core
public class OrderProjectionHandler :
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>
{
    private readonly OrderDbContext _context;

    public OrderProjectionHandler(OrderDbContext context)
    {
        _context = context;
    }

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        var order = new Order
        {
            Id = ctx.Event.OrderId,
            CustomerId = ctx.Event.CustomerId,
            Amount = ctx.Event.Amount,
            Status = OrderStatus.Created
        };

        _context.Orders.Add(order);
        await _context.SaveChangesAsync(ctx.CancellationToken);
    }

    public async Task Handle(EventContext<OrderPlaced> ctx)
    {
        var order = await _context.Orders.FindAsync(ctx.Event.OrderId);
        if (order != null)
        {
            order.Status = OrderStatus.Placed;
            await _context.SaveChangesAsync(ctx.CancellationToken);
        }
    }
}

// Registration
services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
        options.MigrationStrategy = new NoMigrationStrategy();  // ✅ You manage migrations
    })
    .WithChannelSubscriptions(channel => channel
        .Add<OrderProjectionHandler>(mode: SubscriptionMode.Async)));  // ✅ Just a handler
```

**EF Core migrations:**
```bash
dotnet ef migrations add AddOrderProjections --context OrderDbContext
dotnet ef database update --context OrderDbContext
```

#### **Example 2: Dapper with Custom SQL**

```csharp
public class OrderProjectionHandler : IHandleEvent<OrderCreated>
{
    private readonly string _connectionString;

    public async Task Handle(EventContext<OrderCreated> ctx)
    {
        await using var connection = new NpgsqlConnection(_connectionString);

        // Your custom SQL, your schema, your columns
        const string sql = @"
            INSERT INTO read_models.orders (id, customer_id, amount, status, created_at)
            VALUES (@Id, @CustomerId, @Amount, @Status, NOW())
            ON CONFLICT (id) DO NOTHING";

        await connection.ExecuteAsync(sql, new
        {
            Id = ctx.Event.OrderId,
            CustomerId = ctx.Event.CustomerId,
            Amount = ctx.Event.Amount,
            Status = "Created"
        });
    }
}
```

**Migrations:** Use Flyway, Liquibase, or plain SQL scripts.

#### **Example 3: Hybrid (EventStore JSONB + EF Core)**

```csharp
// Keep the JSONB projection for simple queries
.AddPostgresProjection<OrderEventStore, OrderSummaryProjection, Guid, OrderSummary, OrderSummaryProjector>(
    mode: SubscriptionMode.Sync)  // Fast, eventually consistent

// Use EF Core for complex queries
.Add<OrderDetailsHandler>(mode: SubscriptionMode.Async);  // Normalized, relational
```

---

## 3. Clear Separation of Concerns

### EventStore Responsibilities

Alberto manages:
- ✅ `{schema}.events` table
- ✅ `{schema}.subscription_checkpoints` table
- ✅ `{schema}.subscription_poison_pills` table
- ✅ Indexes on events table
- ✅ Migration strategy (pluggable)

### Your Responsibilities

You manage:
- ✅ Projection schemas (any structure you want)
- ✅ EF Core DbContext (if using EF)
- ✅ Custom tables, views, indexes
- ✅ Data migrations
- ✅ Backup/restore strategies

---

## 4. Migration Strategy Comparison

| Strategy | Dev | Staging | Production | Review | Rollback |
|----------|-----|---------|------------|--------|----------|
| **NoMigration** | ❌ | ✅ | ✅ | ✅ | ✅ |
| **ScriptOnly** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **AutoMigration** | ✅ | ❌ | ❌ | ❌ | ❌ |
| **EF Core** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Flyway/Liquibase** | ✅ | ✅ | ✅ | ✅ | ✅ |

---

## 5. ✅ Implementation Status

### ✅ Migration Strategies (Implemented)

```csharp
public interface IMigrationStrategy
{
    Task EnsureSchemaAsync(string schema, string connectionString, CancellationToken ct);
}

public class NoMigrationStrategy : IMigrationStrategy { ... }
public class ScriptOnlyMigrationStrategy : IMigrationStrategy { ... }
public class AutoMigrationStrategy : IMigrationStrategy { ... }
```

### ✅ Projection Auto-Migration Removed (Implemented)

- Removed `AddPostgresProjection` - no longer available
- Removed automatic projection table creation
- Users manage projections via EF Core, Dapper, or custom SQL
- See [EF_CORE_INTEGRATION.md](examples/EF_CORE_INTEGRATION.md) for migration guide

### ✅ Migration Templates (Implemented)

- Library ships embedded SQL templates
- Templates generated to `./Migrations/EventStore/{schema}/`
- Idempotent migrations with `__alberto_schema_version` tracking
- Users can review and modify generated SQL before deployment

### ✅ Documentation (Implemented)

- ✅ EF Core integration example ([EF_CORE_INTEGRATION.md](examples/EF_CORE_INTEGRATION.md))
- ✅ Migration strategy guide (this document)
- ✅ Architecture recommendations

---

## 6. Benefits of This Approach

### For Development
```csharp
// Auto-generates and applies migrations on first run
// Default: AutoMigrationStrategy
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(options => {
        options.ConnectionString = connectionString;
        options.Schema = "orders";
    }));
```

### For Production
```csharp
// Full control - no auto-migrations, manual deployment
options.MigrationStrategy = new NoMigrationStrategy();
// Migrations deployed via your CI/CD pipeline
```

### For Projections
```csharp
// Full control - EF Core, Dapper, plain SQL, or any database
services.AddDbContext<OrderDbContext>(...);
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithChannelSubscriptions(channel => channel
        .Add<OrderProjectionHandler>(mode: SubscriptionMode.Async)));
```

---

## 7. Best Practices

### ✅ DO

1. **Use `AutoMigrationStrategy` in development** - Fast iteration, auto-generates migrations
2. **Use `NoMigrationStrategy` in production** - Manage migrations via deployment pipeline
3. **Review generated SQL before first deployment** - Located in `./Migrations/EventStore/{schema}/`
4. **Use EF Core for complex projections** - Normalized schemas, navigation properties, LINQ
5. **Separate EventStore schema from projection schemas** - Different lifecycle, different ownership
6. **Commit generated migrations to source control** - Version control your schema

### ❌ DON'T

1. **Don't use `AutoMigrationStrategy` in production** - No review, no rollback control
2. **Don't delete generated migration files** - Library expects them on disk
3. **Don't mix EventStore schema with projection schema** - Use separate schemas
4. **Don't modify `__alberto_schema_version` manually** - Managed by migrations

---

## Conclusion

**Current Architecture (v1.0):**

- **EventStore schema**: Managed by Alberto via pluggable `IMigrationStrategy`
  - Embedded SQL templates shipped with library
  - Generated to `./Migrations/EventStore/{schema}/` for review
  - Idempotent migrations with version tracking

- **Projection schema**: Managed by YOU
  - Use EF Core, Dapper, plain SQL, or any database
  - Full control over schema, indexes, and migrations
  - See [EF_CORE_INTEGRATION.md](examples/EF_CORE_INTEGRATION.md)

**Key Benefits:**
- ✅ Control over when/how EventStore migrations run
- ✅ Flexibility for projection storage (no forced JSONB)
- ✅ Clear separation of concerns
- ✅ Production-ready deployment patterns
- ✅ Idempotent migrations safe to re-run
