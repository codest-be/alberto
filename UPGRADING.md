# Upgrading Alberto DCB

## Breaking changes in this release

### 1. Admin package removed — use the CLI instead

`Alberto.Dcb.Admin` and the embedded Angular admin UI have been removed. Replace with the `alberto` .NET global tool:

```bash
dotnet tool install -g Alberto.Cli
```

**Remove from your modules:**
```csharp
// Before
builder.AddAlbertoModule(module => module
    .WithPostgres(...)
    .WithAdmin(admin => { admin.Title = "Orders"; }));   // ← remove

// After
builder.AddAlbertoModule(module => module
    .WithPostgres(...));
```

**Remove from your app startup:**
```csharp
// Before
builder.Services.AddPostgresAdminSubscriptions();        // ← remove
app.MapDcbAdmin();                                       // ← remove
```

**Remove from your `.csproj`:**
```xml
<!-- remove this -->
<PackageReference Include="Alberto.Dcb.Admin" />
```

**CLI quick reference:**
```bash
alberto status                                  # system overview
alberto processor <id>                          # processor details
alberto checkpoints                             # all checkpoints
alberto dead-letters --processor <id>           # dead letters
alberto events --type <type> --limit 50        # event browser
alberto projections list <type>                # projection states
alberto tenants                                # tenant leases
alberto ops rebuild <id>                       # reset checkpoint → full replay
alberto ops checkpoint reset <id>              # reset checkpoint
alberto ops dead-letters retry-rewind <id>     # rewind to earliest dead letter
alberto ops tenants release                    # release all tenant leases
```

Connection defaults to `Host=localhost;Database=postgres`. Override via `--url`, `ALBERTO_URL` env var, or `.alberto/config.json`.

---

### 2. Multi-tenant apps must opt in to tenancy

Single-tenant is now the default. If your app uses `X-Tenant-Id` header routing and per-tenant event isolation, add `.WithTenancy()`:

```csharp
// Before (implicitly multi-tenant)
builder.AddAlbertoModule(module => module
    .WithPostgres(...));

// After (explicit opt-in)
builder.AddAlbertoModule(module => module
    .WithPostgres(...)
    .WithTenancy());
```

Single-tenant apps gain a simpler schema (no `tenant_id` column). Run `PostgresMigrator.Migrate(connectionString, singleTenant: true)` to use the single-tenant migration set.

---

### 4. New database migrations (run automatically on startup)

Five new migrations are applied automatically when the application starts:

| # | Name | What it adds |
|---|------|-------------|
| 013 | DeadLetterPosition | `global_position` column on dead letters |
| 014 | Outbox | `outbox_entries` table (if using `Alberto.Dcb.Messaging`) |
| 015 | TenantAssignments | `tenant_assignments` table for consistent hash ring |
| 016 | FencedCheckpoint | `save_checkpoint_if_lease_held` SQL function |

No manual steps required — `PostgresMigrator.Migrate()` handles them.

---

## Deprecations (still work, emit warnings)

These APIs are obsolete and will be removed in a future version.

### Old projection API → `DeclareProjection`

```csharp
// Before (still works, CS0618 warning)
public class OrderSummaryProjection : Projection<OrderSummary>,
    IProject<OrderSummary, OrderPlaced>
{
    public ProjectionResult<OrderSummary> Apply(OrderSummary state, IEventEnvelope<OrderPlaced> envelope) { ... }
}

consumer.AddProjection<OrderSummary, OrderSummaryProjection>(...);

// After
var declaration = DeclareProjection.For<OrderSummary>()
    .WithId(e => e.ParseEvent<OrderPlaced>()?.OrderId.ToString())
    .On<OrderPlaced>((state, e) => state with { ... })
    .Build();

consumer.AddProjection(declaration, ...);
```

### Old filter API → middleware

```csharp
// Before (still works, CS0618 warning)
consumer.AddFilter<MyConsumeFilter>();

// After
consumer.WithMiddleware(ConsumeMiddlewares.RetryAndDeadLetter());
consumer.WithMiddleware(async (ctx, next) => { /* custom logic */ await next(); });
```

---

## New features

- **`DcbQuery.For()`** — shorthand for single-tag queries: `DcbQuery.For("order", orderId)`
- **`Evolver<TState>`** — functional state reconstitution without projections
- **`DecisionResult<TEvent>`** — typed result type for deciders
- **Lifecycle callbacks** — `consumer.OnProjected(...)` / `consumer.OnRebuildComplete(...)`
- **Outbox/Messaging** — `Alberto.Dcb.Messaging` package for transactional outbox
- **Consistent hash ring** — `consumer.WithConsistentHashRing(tenantRing)` for deterministic tenant distribution
- **Fencing tokens** — zombie checkpoint writes rejected when lease expires (automatic in tenant-distributed mode)
