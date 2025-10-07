# Alberto.CQRS

Opinionated CQRS framework with commands, queries, validation, and auto-registration.

## What's Included

- **Result<T> / Problem / Decision<T>** - Functional error handling types
- **ICommand / ICommandHandler** - Command pattern with handlers
- **IQuery / IQueryHandler** - Query pattern with handlers
- **CommandExecutor / QueryExecutor** - Execute commands/queries with validation
- **IValidator / FluentValidationAdapter** - Validation pipeline integration
- **ModuleBuilder** - Auto-registration via assembly scanning

## Quick Start

### 1. Define a Command

```csharp
public record CreateOrderCommand : ICommand
{
    public required decimal Amount { get; init; }
    public required string CustomerId { get; init; }
}

public class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.CustomerId).NotEmpty();
    }
}

public class CreateOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<CreateOrderCommand, OrderId>
{
    public async Task<Result<OrderId>> Handle(
        CreateOrderCommand cmd,
        CancellationToken ct)
    {
        var id = OrderId.Create();
        var query = new StreamQuery().WithTags(new EventTag(Tags.Order, id.Value));

        var (events, version) = await eventStore.Load(query, ct);
        var projector = new OrderProjector();
        var state = projector.Evolve(events);

        // Make decision
        var decision = OrderDecisions.Create(state, cmd, id);
        if (decision.IsError)
            return Result<OrderId>.Fail(decision.Problems);

        // Persist
        await eventStore.Persist(query, version, decision.Events, ct);
        return decision.Value;
    }
}
```

### 2. Register Module

```csharp
// Module registration
public static IServiceCollection AddOrdersModule(
    this IServiceCollection services,
    IConfiguration config)
{
    // 1. Register EventStore
    services.AddPostgresEventStore<OrderEventStore>(options => {
        options.ConnectionString = config.GetConnectionString("db");
        options.Schema = "orders";
    });

    // 2. Register CQRS module (auto-discovers handlers/validators)
    services.AddCqrsModule<OrderEventStore>(
        typeof(CreateOrderCommand).Assembly);

    return services;
}
```

### 3. Use in Controller

```csharp
[ApiController]
[Route("api/orders")]
public class OrdersController(
    [FromKeyedServices(typeof(OrderEventStore).FullName)] CommandExecutor commands,
    [FromKeyedServices(typeof(OrderEventStore).FullName)] QueryExecutor queries)
{
    [HttpPost]
    public async Task<IActionResult> CreateOrder(CreateOrderCommand cmd)
    {
        var result = await commands.Execute<CreateOrderCommand, OrderId>(cmd);

        return result.IsSuccess
            ? Ok(new { OrderId = result.Value })
            : BadRequest(result.Problems);
    }
}
```

## Decision Pattern

Use `Decision` for domain logic that produces events:

```csharp
public static class OrderDecisions
{
    public static Decision<OrderId> Create(
        OrderState state,
        CreateOrderCommand cmd,
        OrderId id)
    {
        if (state.Exists)
            return Problems.OrderAlreadyExists;

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

## Result Pattern

Use `Result` for handler return types:

```csharp
// Success
return Result<OrderId>.Success(id);

// Failure
return Result.Fail("Order not found");
return Result<OrderId>.Fail(new Problem { Code = "NOT_FOUND", Message = "..." });

// Pattern matching
return result.IsSuccess
    ? Ok(result.Value)
    : BadRequest(result.Problems);
```

## Validation

Validators are automatically discovered and executed before handlers:

```csharp
public class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.CustomerId).NotEmpty().MaximumLength(100);
    }
}
```

## Auto-Registration

Assembly scanning registers:

- Command handlers (`ICommandHandler<,>`)
- Query handlers (`IQueryHandler<,>`)
- Validators (`AbstractValidator<>`)

Handlers are keyed by EventStore type for module isolation.

## Design Philosophy

**Opinionated but flexible:**

- Provides full CQRS framework
- Functional error handling (Result/Decision)
- Validation pipeline with FluentValidation
- Auto-registration for convenience

**Optional:**

- Use only what you need
- Can skip validation, auto-registration, etc.
- Works alongside other CQRS frameworks (MediatR, Wolverine)

## Dependencies

- Alberto.EventStore (for event storage)
- FluentValidation (for validation pipeline)
- Microsoft.Extensions.DependencyInjection (for DI integration)
