# Alberto.ComponentTests - Integration Testing Framework

## Overview

Alberto.ComponentTests provides a fluent, step-based testing framework for full-stack integration tests. It integrates
with ASP.NET Core WebApplicationFactory and xUnit v3 to test complete features from HTTP through to the database.

## Design Philosophy

**Feature-focused testing:**

- Test complete user journeys, not individual units
- HTTP → validation → commands → event store → projections
- Step-based organization for readability and reusability
- Clear separation between arrange, act, and assert phases

**No business logic duplication:**

- Unit tests cover decision logic
- Component tests cover integration and happy paths
- Validation tests at API level, not business rules

## Core Concepts

### UseCase Pattern

The `UseCase` class provides a fluent API for building test scenarios:

```csharp
await UseCase()
    .Arrange(
        new CreateOrder(orderId, amount: 100m, customerId: "customer1"))
    .Act(
        new PlaceOrder(orderId))
    .Assert(
        new HttpSuccessResponse(),
        new OrderIsPlaced(orderId))
    .RunAsync();
```

**Flow:**

1. **Arrange**: Setup actions (create test data, no verification)
2. **Act**: The action under test
3. **Assert**: Verify outcomes
4. **RunAsync**: Execute all steps in order

### IStep Interface

All test actions and assertions implement `IStep`:

```csharp
public interface IStep
{
    Task Execute(ScenarioContext context, CancellationToken cancellationToken = default);
}
```

**Step types:**

- **Action steps**: Perform operations (CreateOrder, PlaceOrder, etc.)
- **Assertion steps**: Verify outcomes (HttpSuccessResponse, OrderIsPlaced, etc.)

### ScenarioContext

The `ScenarioContext` manages test state and provides access to services:

```csharp
public class ScenarioContext
{
    public HttpClient HttpClient { get; }
    public IServiceProvider ServiceProvider { get; }
    public HttpResponseMessage? LastResponse { get; set; }
    public Dictionary<string, object> State { get; } = new();

    public T GetService<T>() where T : notnull;
    public T GetKeyedService<T>(string key) where T : notnull;
}
```

**Usage:**

- Access HttpClient for API calls
- Get services from DI container
- Store/retrieve state between steps
- Access last HTTP response

## Testing Patterns

### 1. Happy Path Integration

```csharp
[Fact]
public async Task Should_create_and_place_order()
{
    var orderId = Guid.NewGuid();

    await UseCase()
        .Arrange(
            new CreateOrder(orderId, 100m, "customer1"))
        .Act(
            new PlaceOrder(orderId))
        .Assert(
            new HttpSuccessResponse(),
            new OrderIsPlaced(orderId))
        .RunAsync();
}
```

### 2. Validation Testing

```csharp
[Fact]
public async Task Should_reject_invalid_amount()
{
    var orderId = Guid.NewGuid();

    await UseCase()
        .Act(
            new CreateOrder(orderId, -10m, "customer1"))  // Invalid amount
        .Assert(
            new HttpFailureResponse(HttpStatusCode.BadRequest),
            new ProblemCodePresent("INVALID_AMOUNT"))
        .RunAsync();
}
```

### 3. Complex Workflows

```csharp
[Fact]
public async Task Should_complete_order_workflow()
{
    var orderId = Guid.NewGuid();

    await UseCase()
        .Arrange(
            new CreateOrder(orderId, 100m, "customer1"),
            new PlaceOrder(orderId))
        .Act(
            new ShipOrder(orderId, "TRACK-123"))
        .Assert(
            new HttpSuccessResponse(),
            new OrderIsShipped(orderId),
            new OrderHasTrackingNumber(orderId, "TRACK-123"))
        .RunAsync();
}
```

### 4. Error Handling

```csharp
[Fact]
public async Task Should_reject_cancelling_shipped_order()
{
    var orderId = Guid.NewGuid();

    await UseCase()
        .Arrange(
            new CreateOrder(orderId, 100m, "customer1"),
            new PlaceOrder(orderId),
            new ShipOrder(orderId, "TRACK-123"))
        .Act(
            new CancelOrder(orderId, "Changed mind"))
        .Assert(
            new HttpFailureResponse(HttpStatusCode.BadRequest),
            new ProblemCodePresent("CANNOT_CANCEL_SHIPPED_ORDER"))
        .RunAsync();
}
```

## Step Organization

### Action Steps (HTTP Calls)

```csharp
public class CreateOrder : IStep
{
    private readonly Guid _orderId;
    private readonly decimal _amount;
    private readonly string _customerId;

    public CreateOrder(Guid orderId, decimal amount, string customerId)
    {
        _orderId = orderId;
        _amount = amount;
        _customerId = customerId;
    }

    public async Task Execute(ScenarioContext context, CancellationToken ct)
    {
        var request = new CreateOrderRequest
        {
            Amount = _amount,
            CustomerId = _customerId
        };

        var response = await context.HttpClient.PostAsJsonAsync(
            $"/api/orders",
            request,
            ct);

        context.LastResponse = response;
    }
}
```

### Assertion Steps (Verification)

```csharp
public class OrderIsPlaced : IStep
{
    private readonly Guid _orderId;

    public OrderIsPlaced(Guid orderId)
    {
        _orderId = orderId;
    }

    public async Task Execute(ScenarioContext context, CancellationToken ct)
    {
        var repository = context.GetService<IProjectionRepository<Guid, Order>>();
        var order = await repository.Get(_orderId, ct);

        order.Should().NotBeNull();
        order!.Status.Should().Be(OrderStatus.Placed);
    }
}
```

### Composite Steps

```csharp
public class CreateAndPlaceOrder : IStep
{
    private readonly Guid _orderId;
    private readonly decimal _amount;
    private readonly string _customerId;

    public CreateAndPlaceOrder(Guid orderId, decimal amount, string customerId)
    {
        _orderId = orderId;
        _amount = amount;
        _customerId = customerId;
    }

    public async Task Execute(ScenarioContext context, CancellationToken ct)
    {
        await new CreateOrder(_orderId, _amount, _customerId).Execute(context, ct);
        await new PlaceOrder(_orderId).Execute(context, ct);
    }
}
```

## Fixture Setup

### ServiceFixture

```csharp
public class ServiceFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Override services for testing
                    // e.g., use in-memory event store
                });
            });

        await Task.CompletedTask;
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public IServiceProvider Services => _factory.Services;

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }
}
```

### Test Class Setup

```csharp
public class OrderTests : IClassFixture<ServiceFixture>
{
    private readonly ServiceFixture _fixture;

    public OrderTests(ServiceFixture fixture)
    {
        _fixture = fixture;
        UseCase.Configure(_fixture.CreateClient, _fixture.Services);
    }

    [Fact]
    public async Task Should_create_order()
    {
        // UseCase has access to HttpClient and Services
        await UseCase()...
    }
}
```

## Built-in Assertions

### HTTP Response Assertions

```csharp
new HttpSuccessResponse()  // 2xx status code
new HttpFailureResponse(HttpStatusCode.BadRequest)  // Specific status
new HttpStatusCode(HttpStatusCode.Created)  // Exact status
```

### Problem Assertions

```csharp
new ProblemCodePresent("ORDER_NOT_FOUND")  // Error code exists
new ProblemMessageContains("does not exist")  // Error message substring
```

### State Assertions

```csharp
new OrderExists(orderId)
new OrderIsPlaced(orderId)
new OrderIsShipped(orderId)
new OrderIsCancelled(orderId)
new OrderHasTrackingNumber(orderId, "TRACK-123")
```

## Best Practices

### 1. Test Feature Workflows, Not Individual Operations

```csharp
// ✅ Good: Test complete workflow
[Fact]
public async Task Should_complete_order_lifecycle()
{
    await UseCase()
        .Arrange(new CreateOrder(...))
        .Act(new PlaceOrder(...), new ShipOrder(...))
        .Assert(new OrderIsShipped(...));
}

// ❌ Avoid: Testing single operations in isolation
[Fact]
public async Task Should_create_order() { ... }

[Fact]
public async Task Should_place_order() { ... }

[Fact]
public async Task Should_ship_order() { ... }
```

### 2. Use Arrange for Setup, Not Assertions

```csharp
// ✅ Good: Arrange sets up state without verification
await UseCase()
    .Arrange(new CreateOrder(...))  // No verification
    .Act(new PlaceOrder(...))
    .Assert(new OrderIsPlaced(...));  // Verify here

// ❌ Avoid: Verifying in arrange phase
await UseCase()
    .Arrange(
        new CreateOrder(...),
        new OrderExists(...))  // Don't verify in arrange
    .Act(...)
```

### 3. Reuse Steps Across Tests

```csharp
// Define reusable steps in Steps/ folder
public class Steps
{
    public static class Orders
    {
        public static CreateOrder Create(Guid id, decimal amount, string customerId) =>
            new(id, amount, customerId);

        public static PlaceOrder Place(Guid id) => new(id);

        public static ShipOrder Ship(Guid id, string tracking) => new(id, tracking);
    }
}

// Use in tests
await UseCase()
    .Arrange(Steps.Orders.Create(id, 100m, "customer1"))
    .Act(Steps.Orders.Place(id))
    .Assert(new OrderIsPlaced(id));
```

### 4. Test Validation at API Level

```csharp
// ✅ Good: Test API validation
[Fact]
public async Task Should_reject_invalid_amount()
{
    await UseCase()
        .Act(new CreateOrder(id, -10m, "customer1"))
        .Assert(
            new HttpFailureResponse(HttpStatusCode.BadRequest),
            new ProblemCodePresent("INVALID_AMOUNT"));
}
```

### 5. Avoid Testing Business Logic

```csharp
// ❌ Avoid: Testing business rules in component tests
// This belongs in unit tests (UnitTests package)
[Fact]
public async Task Should_not_ship_cancelled_order() { ... }

// ✅ Instead: Test happy path + validation
[Fact]
public async Task Should_ship_placed_order() { ... }  // Happy path

[Fact]
public async Task Should_reject_invalid_tracking_number() { ... }  // Validation
```

## File Structure

- `UseCase.cs` - Fluent API for test scenarios
- `ScenarioContext.cs` - Test context and state management
- `ServiceFixture.cs` - WebApplicationFactory setup
- `Steps/IStep.cs` - Step interface
- `Steps/Assertions/` - Built-in assertion steps
- `Steps/Actions/` - Built-in action steps
- `Logger/` - Test output logging

## Performance

**Typical test performance:**

- Single operation: ~50-200ms
- Multi-step workflow: ~200-600ms
- 10-test suite: ~2-5 seconds

**Optimization tips:**

- Use `IClassFixture` to share fixture across tests
- Use in-memory backend for faster tests
- Minimize HTTP roundtrips (batch operations)

## Integration with xUnit v3

```csharp
public class OrderTests : IClassFixture<ServiceFixture>
{
    [Fact]
    public async Task Should_create_order()
    {
        await UseCase()...
    }

    [Theory]
    [InlineData(100.00)]
    [InlineData(500.00)]
    public async Task Should_create_order_with_amount(decimal amount)
    {
        await UseCase()
            .Act(new CreateOrder(Guid.NewGuid(), amount, "customer1"))
            .Assert(new HttpSuccessResponse());
    }
}
```

## Comparison with Other Frameworks

| Framework            | Alberto.ComponentTests      |
|----------------------|-----------------------------|
| **Pattern**          | Step-based UseCase          |
| **Focus**            | Full-stack integration      |
| **Readability**      | High (Arrange-Act-Assert)   |
| **Reusability**      | High (composable steps)     |
| **Setup complexity** | Low (WebApplicationFactory) |
| **Performance**      | Fast (~100-600ms per test)  |

## Common Pitfalls

1. **Testing business logic**: Use unit tests for decision logic
2. **Verification in arrange**: Only verify in assert phase
3. **Not reusing steps**: Define reusable steps for common operations
4. **Slow tests**: Use in-memory backend, minimize HTTP calls
5. **Flaky tests**: Ensure deterministic state, avoid timing dependencies
