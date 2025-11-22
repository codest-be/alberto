# Alberto.UnitTests - Specification Pattern for Business Logic Testing

## Overview

Alberto.UnitTests provides a specification pattern for testing pure business logic in event-sourced systems. It focuses
on testing decisions and projectors without infrastructure, enabling fast feedback and high confidence in domain logic.

## Design Philosophy

**Pure business logic testing:**

- Test decisions in isolation (no I/O, no mocking)
- Fast execution (~100ms for entire test suite)
- Declarative API (Given-When-Then)
- Event-driven assertions

**Clear separation:**

- Unit tests: Business rules and state transitions
- Component tests: Integration and API validation
- No duplication between layers

## Core Patterns

### 1. Stateless Specification

For commands that don't require aggregate history:

```csharp
[Fact]
public void Should_create_order_with_valid_inputs()
{
    new Specification()
        .When(() => OrderDecisions.Create(amount: 100m, customerId: "customer1"))
        .ThenEventOfType<OrderCreated>()
        .WithValue(created => created.Amount == 100m)
        .WithValue(created => created.CustomerId == "customer1");
}

[Fact]
public void Should_fail_with_invalid_amount()
{
    new Specification()
        .When(() => OrderDecisions.Create(amount: -10m, customerId: "customer1"))
        .ThenFailWith(OrderProblems.InvalidAmount);
}
```

**Use cases:**

- Creating new aggregates
- Commands without preconditions
- Pure validation logic

### 2. Stateful Specification

For commands that depend on aggregate state:

```csharp
[Fact]
public void Should_place_order_when_created()
{
    new Specification<OrderState>(new OrderProjector())
        .Given(
            new OrderCreated(orderId, 100m, "customer1"))
        .When(state => OrderDecisions.PlaceOrder(state))
        .ThenEventOfType<OrderPlaced>()
        .WithValue(placed => placed.OrderId == orderId);
}

[Fact]
public void Should_reject_placing_already_placed_order()
{
    new Specification<OrderState>(new OrderProjector())
        .Given(
            new OrderCreated(orderId, 100m, "customer1"),
            new OrderPlaced(orderId))
        .When(state => OrderDecisions.PlaceOrder(state))
        .ThenFailWith(OrderProblems.InvalidStatusForPlacing(OrderStatus.Placed));
}
```

**Use cases:**

- Commands with preconditions
- State-dependent decisions
- Testing state transitions

## API Reference

### Stateless Specification

```csharp
new Specification()
    .When(() => decision)      // Execute decision
    .ThenEventOfType<T>()      // Expect event type T
    .ThenEvents(event1, ...)   // Expect specific events
    .ThenFailWith(problem)     // Expect failure with problem
    .WithValue(predicate);     // Verify event property
```

### Stateful Specification

```csharp
new Specification<TState>(projector)
    .Given(events...)                    // Setup initial state
    .When(state => decision)             // Execute decision with state
    .ThenEventOfType<T>()                // Expect event type T
    .ThenEvents(event1, ...)             // Expect specific events
    .ThenFailWith(problem)               // Expect failure with problem
    .ThenState(predicate);               // Verify final state
```

## Testing Patterns

### 1. Testing Create Operations

```csharp
public class CreateOrder_Should
{
    [Fact]
    public void Succeed_with_valid_inputs()
    {
        var decision = OrderDecisions.Create(100m, "customer1");

        new Specification()
            .When(() => decision)
            .ThenEventOfType<OrderCreated>()
            .WithValue(e => e.Amount == 100m)
            .WithValue(e => e.CustomerId == "customer1");
    }

    [Fact]
    public void Fail_when_amount_is_zero()
    {
        var decision = OrderDecisions.Create(0m, "customer1");

        new Specification()
            .When(() => decision)
            .ThenFailWith(OrderProblems.InvalidAmount);
    }

    [Fact]
    public void Fail_when_customer_is_empty()
    {
        var decision = OrderDecisions.Create(100m, "");

        new Specification()
            .When(() => decision)
            .ThenFailWith(OrderProblems.InvalidCustomer);
    }
}
```

### 2. Testing State Transitions

```csharp
public class PlaceOrder_Should
{
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly OrderProjector _projector = new();

    [Fact]
    public void Succeed_when_order_is_created()
    {
        new Specification<OrderState>(_projector)
            .Given(new OrderCreated(_orderId, 100m, "customer1"))
            .When(state => OrderDecisions.PlaceOrder(state))
            .ThenEventOfType<OrderPlaced>()
            .ThenState(state => state.Status == OrderStatus.Placed);
    }

    [Fact]
    public void Fail_when_order_already_placed()
    {
        new Specification<OrderState>(_projector)
            .Given(
                new OrderCreated(_orderId, 100m, "customer1"),
                new OrderPlaced(_orderId))
            .When(state => OrderDecisions.PlaceOrder(state))
            .ThenFailWith(OrderProblems.InvalidStatusForPlacing(OrderStatus.Placed));
    }

    [Fact]
    public void Fail_when_order_is_shipped()
    {
        new Specification<OrderState>(_projector)
            .Given(
                new OrderCreated(_orderId, 100m, "customer1"),
                new OrderPlaced(_orderId),
                new OrderShipped(_orderId, "TRACK-123"))
            .When(state => OrderDecisions.PlaceOrder(state))
            .ThenFailWith(OrderProblems.InvalidStatusForPlacing(OrderStatus.Shipped));
    }
}
```

### 3. Testing Business Rules

```csharp
public class CancelOrder_Should
{
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly OrderProjector _projector = new();

    [Fact]
    public void Succeed_when_order_is_created()
    {
        new Specification<OrderState>(_projector)
            .Given(new OrderCreated(_orderId, 100m, "customer1"))
            .When(state => OrderDecisions.Cancel(state, "Changed mind"))
            .ThenEventOfType<OrderCancelled>()
            .WithValue(e => e.Reason == "Changed mind");
    }

    [Fact]
    public void Succeed_when_order_is_placed()
    {
        new Specification<OrderState>(_projector)
            .Given(
                new OrderCreated(_orderId, 100m, "customer1"),
                new OrderPlaced(_orderId))
            .When(state => OrderDecisions.Cancel(state, "Customer request"))
            .ThenEventOfType<OrderCancelled>();
    }

    [Fact]
    public void Fail_when_order_is_shipped()
    {
        new Specification<OrderState>(_projector)
            .Given(
                new OrderCreated(_orderId, 100m, "customer1"),
                new OrderPlaced(_orderId),
                new OrderShipped(_orderId, "TRACK-123"))
            .When(state => OrderDecisions.Cancel(state, "Too late"))
            .ThenFailWith(OrderProblems.CannotCancelShippedOrder);
    }

    [Fact]
    public void Fail_when_order_already_cancelled()
    {
        new Specification<OrderState>(_projector)
            .Given(
                new OrderCreated(_orderId, 100m, "customer1"),
                new OrderCancelled(_orderId, "Changed mind"))
            .When(state => OrderDecisions.Cancel(state, "Again?"))
            .ThenFailWith(OrderProblems.OrderAlreadyCancelled);
    }
}
```

### 4. Testing Projectors

```csharp
public class OrderProjector_Should
{
    private readonly OrderProjector _projector = new();

    [Fact]
    public void Set_status_to_created_on_OrderCreated()
    {
        var state = new OrderState();
        var @event = new OrderCreated(orderId, 100m, "customer1");

        var result = _projector.Apply(state, @event);

        result.Status.Should().Be(OrderStatus.Created);
        result.Amount.Should().Be(100m);
        result.CustomerId.Should().Be("customer1");
    }

    [Fact]
    public void Set_status_to_placed_on_OrderPlaced()
    {
        var state = new OrderState { Status = OrderStatus.Created };
        var @event = new OrderPlaced(orderId);

        var result = _projector.Apply(state, @event);

        result.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public void Set_tracking_number_on_OrderShipped()
    {
        var state = new OrderState { Status = OrderStatus.Placed };
        var @event = new OrderShipped(orderId, "TRACK-123");

        var result = _projector.Apply(state, @event);

        result.Status.Should().Be(OrderStatus.Shipped);
        result.TrackingNumber.Should().Be("TRACK-123");
    }
}
```

## Decision Pattern

Decisions are pure functions that return `Decision` or `Decision<T>`:

```csharp
public static class OrderDecisions
{
    public static Decision<OrderId> Create(decimal amount, string customerId)
    {
        // Validate inputs
        if (amount <= 0)
            return OrderProblems.InvalidAmount;

        if (string.IsNullOrWhiteSpace(customerId))
            return OrderProblems.InvalidCustomer;

        // Produce events
        var id = OrderId.Create();
        return Decision<OrderId>.Succeed(
            id,
            new OrderCreated(id.Value, amount, customerId));
    }

    public static Decision PlaceOrder(OrderState state)
    {
        // Check preconditions
        if (state.Status != OrderStatus.Created)
            return OrderProblems.InvalidStatusForPlacing(state.Status);

        // Produce events
        return Decision.Succeed(
            new OrderPlaced(state.Id));
    }

    public static Decision Cancel(OrderState state, string reason)
    {
        // Business rules
        if (state.Status == OrderStatus.Cancelled)
            return OrderProblems.OrderAlreadyCancelled;

        if (state.Status == OrderStatus.Shipped)
            return OrderProblems.CannotCancelShippedOrder;

        if (string.IsNullOrWhiteSpace(reason))
            return OrderProblems.InvalidCancellationReason;

        // Produce events
        return Decision.Succeed(
            new OrderCancelled(state.Id, reason));
    }
}
```

**Why pure functions?**

- Easy to test (no mocking)
- Fast execution (no I/O)
- Predictable (same input = same output)
- Composable (can combine decisions)

## Test Organization

### By Command

```
Orders/
├── CreateOrder_Should.cs
├── PlaceOrder_Should.cs
├── ShipOrder_Should.cs
├── CancelOrder_Should.cs
└── OrderProjector_Should.cs
```

### Test Class Naming

```csharp
// Pattern: {Command}_Should.cs
public class PlaceOrder_Should
{
    [Fact]
    public void Succeed_when_order_is_created() { }

    [Fact]
    public void Fail_when_order_already_placed() { }

    [Fact]
    public void Fail_when_order_is_shipped() { }
}
```

## Performance

**Typical performance:**

- Single test: <1ms
- 15-test suite: ~100ms
- 100-test suite: ~500ms

**Why so fast?**

- Pure functions (no I/O)
- No database access
- No HTTP calls
- No mocking overhead

## Best Practices

### 1. Test All State Transitions

```csharp
// ✅ Good: Test all possible states
[Fact] public void Succeed_when_created() { }
[Fact] public void Fail_when_placed() { }
[Fact] public void Fail_when_shipped() { }
[Fact] public void Fail_when_cancelled() { }
```

### 2. Test All Error Cases

```csharp
// ✅ Good: Test all problem scenarios
[Fact] public void Fail_with_invalid_amount() { }
[Fact] public void Fail_with_invalid_customer() { }
[Fact] public void Fail_with_invalid_reason() { }
```

### 3. Use Descriptive Test Names

```csharp
// ✅ Good: Clear intent
[Fact] public void Should_place_order_when_created() { }

// ❌ Avoid: Vague
[Fact] public void Test1() { }
[Fact] public void PlaceOrder() { }
```

### 4. One Assert Per Test

```csharp
// ✅ Good: Single responsibility
[Fact]
public void Should_set_status_to_placed()
{
    new Specification<OrderState>(projector)
        .Given(new OrderCreated(...))
        .When(state => PlaceOrder(state))
        .ThenState(state => state.Status == OrderStatus.Placed);
}

// ❌ Avoid: Multiple assertions
[Fact]
public void Should_update_order()
{
    // Testing status AND tracking number AND timestamp
}
```

### 5. Test Edge Cases

```csharp
[Fact] public void Should_handle_zero_amount() { }
[Fact] public void Should_handle_empty_customer() { }
[Fact] public void Should_handle_whitespace_reason() { }
[Fact] public void Should_handle_null_tracking_number() { }
```

## File Structure

- `Specification.cs` - Stateless specification pattern
- `Specification<TState>.cs` - Stateful specification pattern
- `SpecificationBase.cs` - Base class with assertions

## Integration with xUnit v3

```csharp
public class PlaceOrder_Should
{
    [Fact]
    public void Succeed_when_order_is_created()
    {
        new Specification<OrderState>(new OrderProjector())
            .Given(new OrderCreated(...))
            .When(state => OrderDecisions.PlaceOrder(state))
            .ThenEventOfType<OrderPlaced>();
    }

    [Theory]
    [InlineData(OrderStatus.Placed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    public void Fail_when_order_is_not_created(OrderStatus invalidStatus)
    {
        var state = new OrderState { Status = invalidStatus };

        new Specification()
            .When(() => OrderDecisions.PlaceOrder(state))
            .ThenFailWith(OrderProblems.InvalidStatusForPlacing(invalidStatus));
    }
}
```

## Comparison with Component Tests

| Aspect           | Unit Tests           | Component Tests  |
|------------------|----------------------|------------------|
| **Scope**        | Business logic       | Full integration |
| **Speed**        | <1ms                 | 100-600ms        |
| **Dependencies** | None                 | HTTP, DB, etc.   |
| **Focus**        | Decision correctness | API + validation |
| **Count**        | ~100-200 tests       | ~10-20 tests     |
| **Maintenance**  | Low                  | Medium           |

## Common Pitfalls

1. **Testing infrastructure**: Use component tests for HTTP/DB
2. **Slow tests**: If tests are slow, you're testing the wrong layer
3. **Mocking**: Pure functions don't need mocks
4. **Complex setup**: If setup is complex, refactor decisions
5. **Duplicate coverage**: Don't test same logic in unit and component tests

## Coverage Recommendations

**Unit tests should cover:**

- ✅ All business rules and constraints
- ✅ All state transitions
- ✅ All error cases (Problems)
- ✅ Edge cases and boundary conditions
- ✅ Projector correctness

**Component tests should cover:**

- ✅ Happy path workflows
- ✅ API validation (FluentValidation rules)
- ✅ HTTP status codes
- ✅ Serialization/deserialization

**Avoid testing twice:**

- ❌ Business rules in component tests (covered by unit tests)
- ❌ HTTP calls in unit tests (no infrastructure)
