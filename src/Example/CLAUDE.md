# Alberto Example Application - Architecture Guide

## Overview

The Example application demonstrates Alberto's event sourcing, CQRS, and projection patterns through a real-world Order and Payment processing system. It showcases multi-module architecture with schema isolation, hybrid subscriptions, and full telemetry integration.

## Module Architecture

The application is organized into two bounded contexts:

### Orders Module (`OrdersModule.cs`)
- **Schema**: `orders`
- **EventStore**: `OrderEventStore`
- **Aggregates**: Order lifecycle (Create → Place → Ship → Cancel)
- **Projections**: `OrderProjection`, `OrderStatisticsProjection`
- **Subscription Mode**: Hybrid (sync for OrderProjection, async for statistics)

### Payments Module (`PaymentsModule.cs`)
- **Schema**: `payments`
- **EventStore**: `PaymentEventStore`
- **Aggregates**: Payment lifecycle (Create → Process)
- **Projections**: `PaymentProjection`
- **Subscription Mode**: Hybrid

## Key Design Patterns

### 1. Multi-Tenant Middleware

```csharp
public class TenantMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "default";
        context.Items["TenantId"] = tenantId;

        await next(context);
    }
}

public class MultiTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public Tenant Tenant
    {
        get
        {
            var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"] as string ?? "default";
            return new Tenant(tenantId);
        }
    }
}
```

**Purpose:**
- Extract tenant ID from HTTP header
- Store in HttpContext for scoped resolution
- Injected into EventStore for tenant isolation

### 2. Decision-Based Command Handlers

Commands use the **Decision pattern** to separate business logic from infrastructure:

```csharp
// Orders/Commands/CreateOrder.cs
public record CreateOrder : ICommand
{
    public required decimal Amount { get; init; }
    public required string CustomerId { get; init; }
}

public class CreateOrderHandler(OrderEventStore eventStore) : ICommandHandler<CreateOrder, OrderId>
{
    public async Task<Result<OrderId>> Handle(CreateOrder cmd, CancellationToken ct)
    {
        var id = OrderId.Create();
        var query = new StreamQuery().WithTags(new EventTag(Tags.Order, id.Value));

        var (events, version) = await eventStore.Load(query, ct);
        var state = events.Aggregate(new OrderState(), (s, e) => OrderProjector.Apply(s, e));

        // Business logic
        var decision = OrderDecisions.Create(state, cmd, id);
        if (decision.IsError)
            return Result<OrderId>.Fail(decision.Problems);

        // Persist
        await eventStore.Persist(query, version, decision.Events, ct);
        return decision.Value;
    }
}

// Orders/OrderDecisions.cs (pure business logic)
public static class OrderDecisions
{
    public static Decision<OrderId> Create(OrderState state, CreateOrder cmd, OrderId id)
    {
        if (state.Exists)
            return OrderProblems.OrderAlreadyExists;

        if (cmd.Amount <= 0)
            return OrderProblems.InvalidAmount;

        return Decision<OrderId>.Succeed(
            id,
            new OrderCreated
            {
                OrderId = id.Value,
                Amount = cmd.Amount,
                CustomerId = cmd.CustomerId
            });
    }
}
```

**Benefits:**
- Pure functions for business logic (easy to test)
- Separation of concerns (decisions vs. persistence)
- Type-safe error handling via `Decision<T>`

### 3. Centralized Problem Definitions

All domain errors in `OrderProblems.cs`:

```csharp
public static class OrderProblems
{
    public static readonly Problem OrderNotFound = new()
    {
        Code = "ORDER_NOT_FOUND",
        Message = "The specified order does not exist."
    };

    public static readonly Problem OrderAlreadyExists = new()
    {
        Code = "ORDER_ALREADY_EXISTS",
        Message = "An order with this ID already exists."
    };

    public static readonly Problem InvalidOrderState = new(string status) => new()
    {
        Code = "INVALID_ORDER_STATE",
        Message = $"Cannot perform this operation. Current status: {status}"
    };
}
```

**Benefits:**
- Single source of truth for error codes
- Consistent error messages
- Easy to document API errors
- Refactoring-friendly (find all usages)

### 4. Projection-Based Read Models

Orders module maintains two projections:

**OrderProjection** (sync, per-order):
```csharp
public class OrderProjector : IProjector<Order>
{
    public Order Apply(Order state, object @event)
    {
        return @event switch
        {
            OrderCreated created => new Order
            {
                Id = created.OrderId,
                Amount = created.Amount,
                CustomerId = created.CustomerId,
                Status = OrderStatus.Created
            },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderShipped shipped => state with
            {
                Status = OrderStatus.Shipped,
                TrackingNumber = shipped.TrackingNumber
            },
            OrderCancelled cancelled => state with
            {
                Status = OrderStatus.Cancelled,
                CancellationReason = cancelled.Reason
            },
            _ => state
        };
    }
}
```

**OrderStatisticsProjection** (async, global):
```csharp
public class OrderStatisticsProjector : IProjector<OrderStatistics>
{
    public OrderStatistics Apply(OrderStatistics state, object @event)
    {
        return @event switch
        {
            OrderCreated => state with { TotalOrders = state.TotalOrders + 1 },
            OrderPlaced => state with { PlacedOrders = state.PlacedOrders + 1 },
            OrderShipped => state with { ShippedOrders = state.ShippedOrders + 1 },
            OrderCancelled => state with { CancelledOrders = state.CancelledOrders + 1 },
            _ => state
        };
    }
}
```

**Why two projections?**
- **OrderProjection**: Per-order details, sync updates for strong consistency
- **OrderStatisticsProjection**: Global statistics, async updates (eventual consistency acceptable)

### 5. Minimal API Endpoints

Uses ASP.NET Core Minimal APIs with endpoint classes:

```csharp
public class CreateOrderEndpoint : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("/api/orders", Handler)
        .WithName("CreateOrder")
        .WithTags("Orders");

    private static async Task<IResult> Handler(
        [FromBody] CreateOrderRequest request,
        [FromKeyedServices(typeof(OrderEventStore).FullName)] CommandExecutor commands,
        CancellationToken ct)
    {
        var result = await commands.Execute<CreateOrder, OrderId>(
            new CreateOrder
            {
                Amount = request.Amount,
                CustomerId = request.CustomerId
            },
            ct);

        return result.IsSuccess
            ? Results.Created($"/api/orders/{result.Value.Value}", new { OrderId = result.Value.Value })
            : Results.BadRequest(result.Problems);
    }
}
```

**Benefits:**
- Clear endpoint organization
- Keyed service resolution for module isolation
- Type-safe request/response contracts

### 6. Connection Pooling Configuration

```csharp
public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
{
    services.AddModule<OrderEventStore>("orders", module => module
        .WithPostgres(options =>
        {
            var baseConnectionString = configuration.GetConnectionString("alberto-db");
            options.ConnectionString =
                $"{baseConnectionString};Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300;Connection Pruning Interval=10";
            options.Schema = "orders";
        })
        .WithMultiTenancy<MultiTenantContext>()
        .WithChannelSubscriptions(channel => channel
            .ConfigureSync(options => options.AllowParallelExecution = true)
            .ConfigureAsync(options =>
            {
                options.MinPollingIntervalMs = 100;
                options.MaxRetries = 5;
                options.MaxPageSize = 100;
            })
            .WithFilter<LoggingFilter>()
            .AddPostgresProjection<OrderEventStore, OrderProjectionSubscription, Guid, Order, OrderProjector>(
                mode: SubscriptionMode.Hybrid)
            .AddPostgresProjection<OrderEventStore, OrderStatisticsSubscription, string, OrderStatistics, OrderStatisticsProjector>(
                mode: SubscriptionMode.Async))
        .WithCQRS(typeof(CreateOrder).Assembly)
        .WithTelemetry());

    return services;
}
```

**Configuration highlights:**
- Connection pooling: Min=5, Max=30 per module
- Hybrid subscriptions: Sync for critical projections, async for statistics
- CQRS auto-registration: Scans assembly for handlers/validators
- OpenTelemetry: Full distributed tracing

### 7. Aspire Integration

Application orchestration via .NET Aspire:

```csharp
// AppHost.cs
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage("postgres", "17-alpine")
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("alberto-db");

var example = builder.AddProject<Projects.Alberto_Example>("api")
    .WithReference(postgres);

builder.AddProject<Projects.Alberto_Example_LoadTests>("load-tests")
    .WithReference(example)
    .WithEnvironment("BASE_URL", example.GetEndpoint("http"));

builder.Build().Run();
```

**Benefits:**
- Service discovery
- Automatic connection string injection
- Container orchestration
- Load test integration

## Testing Strategy

### Unit Tests (Example.UnitTests)
- Test pure business logic (decisions)
- Specification pattern for stateful/stateless scenarios
- Fast feedback (~100ms for all tests)

### Component Tests (Example.ComponentTests)
- Test full HTTP → CQRS → EventStore pipeline
- Happy path + validation scenarios
- WebApplicationFactory integration

### Load Tests (Example.LoadTests)
- k6-based load testing
- Full lifecycle tests (Create → Place → Pay → Ship)
- Multiple profiles: smoke, load, stress, breakpoint

## File Organization

```
Modules/
├── Orders/
│   ├── OrdersModule.cs              # Module registration
│   ├── OrderEventStore.cs           # EventStore factory
│   ├── OrderProblems.cs             # Centralized error definitions
│   ├── Tags.cs                      # Event tag constants
│   ├── Commands/                    # Command definitions + handlers
│   ├── Queries/                     # Query definitions + handlers
│   ├── Events/                      # Event definitions
│   ├── Projections/                 # Projectors + subscriptions
│   ├── Api/
│   │   ├── Contracts/               # DTOs
│   │   └── Endpoints/               # Minimal API endpoints
│   └── Filters/                     # Custom consume filters
└── Payments/
    └── (same structure)
```

## Key Takeaways

1. **Module isolation**: Each bounded context has its own schema, EventStore, and projections
2. **Decision pattern**: Pure business logic separated from infrastructure
3. **Hybrid subscriptions**: Balance between consistency and performance
4. **Centralized errors**: Single source of truth for problem definitions
5. **Type safety**: Strong typing throughout (commands, events, projections)
6. **Observability**: Full OpenTelemetry integration for distributed tracing
