# Alberto.CQRS - CQRS Framework Architecture

## Overview

Alberto.CQRS is an opinionated CQRS framework that integrates seamlessly with Alberto EventStore. It provides commands,
queries, validation, functional error handling, and automatic registration via assembly scanning.

## Design Philosophy

**Opinionated but composable:**

- Strong conventions for command/query handling
- Functional error handling (Result/Problem/Decision)
- FluentValidation integration
- Auto-registration for convenience
- Module isolation via keyed services

**Optional by design:**

- Use only what you need
- Works alongside MediatR, Wolverine, etc.
- Can use EventSourcing without CQRS

## Core Patterns

### 1. Command Pattern

Commands represent write operations that change system state:

```csharp
// Command definition
public record PlaceOrderCommand(Guid OrderId) : ICommand;

// Validator (optional)
public class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

// Handler
public class PlaceOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<PlaceOrderCommand>
{
    public async Task<Result> Handle(PlaceOrderCommand cmd, CancellationToken ct)
    {
        var query = new StreamQuery([new EventTag(Tags.Order, cmd.OrderId.ToString())]);
        var (events, version) = await eventStore.Load(query, ct);
        var state = events.Aggregate(new OrderState(), (s, e) => _projector.Apply(s, e));

        var decision = OrderDecisions.PlaceOrder(state);
        if (decision.IsError)
            return Result.Fail(decision.Problems);

        await eventStore.Persist(query, version, decision.Events, ct);
        return Result.Success();
    }
}
```

### 2. Query Pattern

Queries represent read operations that return data:

```csharp
// Query definition
public record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

// Validator (optional)
public class GetOrderValidator : AbstractValidator<GetOrderQuery>
{
    public GetOrderValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

// Handler
public class GetOrderHandler(IProjectionRepository<Guid, Order> repository)
    : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(GetOrderQuery query, CancellationToken ct)
    {
        var order = await repository.Get(query.OrderId, ct);

        if (order == null)
            return OrderProblems.OrderNotFound(query.OrderId);

        return new OrderDto
        {
            Id = order.Id,
            Status = order.Status,
            Amount = order.Amount
        };
    }
}
```

### 3. Decision Pattern

Decisions represent business logic that produces events:

```csharp
public static class OrderDecisions
{
    public static Decision PlaceOrder(OrderState state)
    {
        // Validate preconditions
        if (state.Status != OrderStatus.Created)
            return OrderProblems.InvalidStatusForPlacing(state.Status);

        // Produce events
        return Decision.Succeed(new OrderPlaced
        {
            OrderId = state.Id,
            PlacedAt = DateTimeOffset.UtcNow
        });
    }

    public static Decision<OrderId> CreateOrder(decimal amount, string customerId)
    {
        // Validate inputs
        if (amount <= 0)
            return OrderProblems.InvalidAmount;

        // Produce events with value
        var id = OrderId.Create();
        return Decision<OrderId>.Succeed(
            id,
            new OrderCreated
            {
                OrderId = id.Value,
                Amount = amount,
                CustomerId = customerId
            });
    }
}
```

**Why Decisions?**

- **Pure functions**: Easy to test, no side effects
- **Separation**: Business logic separate from infrastructure
- **Type-safe**: Compile-time guarantees
- **Testable**: No mocking, just call the function

### 4. Result/Problem Pattern

Functional error handling without exceptions:

```csharp
// Result with value
Result<OrderId> result = orderDecision.Value;
if (result.IsSuccess)
{
    var orderId = result.Value;  // Type-safe access
}

// Result without value
Result result = await commandExecutor.Execute<PlaceOrderCommand>(cmd);
if (result.IsFailure)
{
    var problems = result.Problems;  // List<Problem>
    // Return BadRequest(problems) to client
}

// Pattern matching
return result.IsSuccess
    ? Ok(result.Value)
    : BadRequest(result.Problems);
```

**Problem structure:**

```csharp
public record Problem(string Code, string Message)
{
    public static Problem Create(string code, string message) => new(code, message);
}

// Centralized problems
public static class OrderProblems
{
    public static Problem OrderNotFound(Guid id) =>
        Problem.Create("ORDER_NOT_FOUND", $"Order {id} does not exist");

    public static Problem InvalidStatusForPlacing(OrderStatus status) =>
        Problem.Create("INVALID_ORDER_STATUS",
            $"Order must be Created to be placed. Current: {status}");
}
```

## Auto-Registration

Assembly scanning automatically registers:

- Command handlers (`ICommandHandler<,>`)
- Query handlers (`IQueryHandler<,>`)
- Validators (`AbstractValidator<>`)

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithPostgres(...)
    .WithCQRS(cqrs => cqrs
        .ScanAssembly(typeof(OrdersModule).Assembly)  // Scan for handlers/validators
        .WithTelemetry()));  // Optional OpenTelemetry
```

**How it works:**

1. Scans assembly for types implementing `ICommandHandler<,>` or `IQueryHandler<,>`
2. Registers handlers as **keyed services** using EventStore type as key
3. Registers validators for automatic validation
4. Wires up CommandExecutor and QueryExecutor

## Module Isolation

Each EventStore module gets its own CQRS pipeline:

```csharp
// Orders module
services.AddModule<OrderEventStore>("orders", module => module
    .WithCQRS(typeof(CreateOrder).Assembly));

// Payments module
services.AddModule<PaymentEventStore>("payments", module => module
    .WithCQRS(typeof(CreatePayment).Assembly));

// Injection: Type-safe, module-specific
public class OrdersController(
    [FromKeyedServices(typeof(OrderEventStore).FullName)] CommandExecutor orderCommands,
    [FromKeyedServices(typeof(PaymentEventStore).FullName)] CommandExecutor paymentCommands)
{
    // orderCommands only executes Order commands
    // paymentCommands only executes Payment commands
}
```

**Benefits:**

- No cross-module command execution
- Independent handler registration
- Clear boundaries
- Type-safe resolution

## Validation Pipeline

Validators run automatically before handlers:

```csharp
// Validator
public class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .WithErrorCode("INVALID_AMOUNT");

        RuleFor(x => x.CustomerId)
            .NotEmpty()
            .WithErrorCode("INVALID_CUSTOMER");
    }
}

// Execution flow:
// 1. CommandExecutor.Execute() called
// 2. Validator runs (if registered)
// 3. If validation fails: return Result.Fail(validationProblems)
// 4. If validation passes: handler executes
```

**Validation result:**

```csharp
var result = await commands.Execute<CreateOrder, OrderId>(
    new CreateOrder { Amount = -10, CustomerId = "" });

// result.Problems:
// [
//   { Code: "INVALID_AMOUNT", Message: "..." },
//   { Code: "INVALID_CUSTOMER", Message: "..." }
// ]
```

## Telemetry Integration

OpenTelemetry traces for all commands/queries:

```csharp
services.AddModule<OrderEventStore>("orders", module => module
    .WithCQRS(cqrs => cqrs
        .ScanAssembly(...)
        .WithTelemetry()));  // Adds OpenTelemetry

// Automatic traces:
// - Command execution (commandExecutor.Execute)
// - Query execution (queryExecutor.Execute)
// - Validation
// - Handler execution
// - Parent-child relationships with EventStore operations
```

## API Integration

### Minimal APIs

```csharp
public static class CreateOrderEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("/api/orders", Handler);

    private static async Task<IResult> Handler(
        [FromBody] CreateOrderRequest request,
        [FromKeyedServices(typeof(OrderEventStore).FullName)] CommandExecutor commands)
    {
        var result = await commands.Execute<CreateOrder, OrderId>(
            new CreateOrder
            {
                Amount = request.Amount,
                CustomerId = request.CustomerId
            });

        return result.IsSuccess
            ? Results.Created($"/api/orders/{result.Value}", result.Value)
            : Results.BadRequest(result.Problems);
    }
}
```

### MVC Controllers

```csharp
[ApiController]
[Route("api/orders")]
public class OrdersController(
    [FromKeyedServices(typeof(OrderEventStore).FullName)] CommandExecutor commands,
    [FromKeyedServices(typeof(OrderEventStore).FullName)] QueryExecutor queries)
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderCommand cmd)
    {
        var result = await commands.Execute<CreateOrder, OrderId>(cmd);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Problems);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var result = await queries.Execute<GetOrder, OrderDto>(new GetOrder(id));
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Problems);
    }
}
```

## Testing Strategies

### Unit Testing Decisions

```csharp
[Fact]
public void Should_place_order_when_status_is_created()
{
    // Arrange
    var state = new OrderState { Status = OrderStatus.Created };

    // Act
    var decision = OrderDecisions.PlaceOrder(state);

    // Assert
    decision.IsSuccess.Should().BeTrue();
    decision.Events.Should().ContainSingle<OrderPlaced>();
}

[Fact]
public void Should_fail_when_order_already_placed()
{
    // Arrange
    var state = new OrderState { Status = OrderStatus.Placed };

    // Act
    var decision = OrderDecisions.PlaceOrder(state);

    // Assert
    decision.IsError.Should().BeTrue();
    decision.Problems.Should().Contain(OrderProblems.InvalidStatusForPlacing(OrderStatus.Placed));
}
```

### Component Testing Handlers

```csharp
[Fact]
public async Task Should_execute_command_successfully()
{
    // Arrange
    await UseCase()
        .Arrange(new CreateOrder(orderId, 100m, "customer1"))
        .Act(new PlaceOrder(orderId))
        .Assert(new HttpSuccessResponse(), new OrderIsPlaced(orderId))
        .RunAsync();
}
```

## File Structure

- `Commands/ICommand.cs` - Command marker interface
- `Commands/ICommandHandler.cs` - Command handler interface
- `Commands/CommandExecutor.cs` - Command execution pipeline
- `Queries/IQuery.cs` - Query marker interface
- `Queries/IQueryHandler.cs` - Query handler interface
- `Queries/QueryExecutor.cs` - Query execution pipeline
- `Results/Result.cs` - Result type for success/failure
- `Results/Problem.cs` - Error representation
- `Results/Decision.cs` - Decision pattern for domain logic
- `Validation/IValidator.cs` - Validation abstraction
- `Validation/FluentValidationAdapter.cs` - FluentValidation integration
- `Registration/CqrsModuleBuilder.cs` - Auto-registration logic
- `ModuleBuilderExtensions.cs` - `.WithCQRS()` extension

## Best Practices

1. **Pure decisions**: Keep business logic in decision methods (no I/O)
2. **Centralized problems**: Define all errors in `{Module}Problems.cs`
3. **Validate early**: Use FluentValidation for input validation
4. **Return specific errors**: Use typed Problem instances, not strings
5. **Test decisions**: Unit test business logic without handlers
6. **Test handlers**: Component test full pipeline with WebApplicationFactory
7. **Use keyed services**: Inject module-specific executors
8. **Avoid exceptions**: Use Result pattern for control flow

## Common Pitfalls

1. **Not using keyed services**: Injecting wrong module's executor
2. **Testing handlers instead of decisions**: Slow tests with lots of mocking
3. **Throwing exceptions in decisions**: Use Result.Fail() instead
4. **Not validating inputs**: Missing FluentValidation validators
5. **Generic error messages**: Use specific Problem instances
