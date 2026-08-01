using System.Text.Json;
using Alberto.Subscriptions;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for <see cref="ProjectionDeclaration{TState}"/> and its builder.
/// Demonstrates that projections are pure and testable without infrastructure.
/// </summary>
public class ProjectionSpecificationTests
{
    #region Test Events

    [EventType("order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    [EventType("order-confirmed")]
    public record OrderConfirmed(Guid OrderId) : IEvent;

    [EventType("order-cancelled")]
    public record OrderCancelled(Guid OrderId) : IEvent;

    [EventType("order-note-added")]
    public record OrderNoteAdded(Guid OrderId, string Note) : IEvent;

    #endregion

    #region Test State

    public record OrderSummary
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
        public string Status { get; init; } = "";
    }

    #endregion

    #region Test Projection

    private static ProjectionDeclaration<OrderSummary> Declaration() =>
        DeclareProjection.For<OrderSummary>("order-summary")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => new OrderSummary
                {
                    OrderId = e.OrderId,
                    Amount = e.Amount,
                    Status = "Created"
                })
            .On<OrderConfirmed>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state with { Status = "Confirmed" })
            .On<OrderCancelled>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => ProjectionResults.Delete<OrderSummary>())
            .Build();

    #endregion

    #region Handled Event Types

    [Fact]
    public void HandledEventTypes_ShouldContainDeclaredTypes()
    {
        var declaration = Declaration();

        Assert.Contains("order-created", declaration.HandledEventTypes);
        Assert.Contains("order-confirmed", declaration.HandledEventTypes);
        Assert.Contains("order-cancelled", declaration.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ShouldNotContainUndeclaredTypes()
    {
        var declaration = Declaration();

        Assert.DoesNotContain("order-note-added", declaration.HandledEventTypes);
    }

    #endregion

    #region Apply Tests

    [Fact]
    public void Apply_OrderCreated_ShouldReturnSetWithNewState()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m));

        var result = declaration.Apply(new OrderSummary(), envelope, ProjectionContext.FromEnvelope(envelope));

        Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        var set = (ProjectionResult<OrderSummary>.Set)result;
        Assert.Equal(orderId, set.State.OrderId);
        Assert.Equal(100m, set.State.Amount);
        Assert.Equal("Created", set.State.Status);
    }

    [Fact]
    public void Apply_OrderConfirmed_ShouldReturnSetWithUpdatedStatus()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();
        var existingState = new OrderSummary { OrderId = orderId, Amount = 100m, Status = "Created" };
        var envelope = CreateEnvelope(new OrderConfirmed(orderId));

        var result = declaration.Apply(existingState, envelope, ProjectionContext.FromEnvelope(envelope));

        Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        var set = (ProjectionResult<OrderSummary>.Set)result;
        Assert.Equal("Confirmed", set.State.Status);
        Assert.Equal(100m, set.State.Amount); // Amount preserved
    }

    [Fact]
    public void Apply_OrderCancelled_ShouldReturnDelete()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();
        var existingState = new OrderSummary { OrderId = orderId, Amount = 100m, Status = "Created" };
        var envelope = CreateEnvelope(new OrderCancelled(orderId));

        var result = declaration.Apply(existingState, envelope, ProjectionContext.FromEnvelope(envelope));

        Assert.IsType<ProjectionResult<OrderSummary>.Delete>(result);
    }

    [Fact]
    public void Apply_TypedOverload_ShouldNotNeedAnEnvelope()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();

        var result = declaration.Apply(
            new OrderSummary(),
            new OrderCreated(orderId, 42m),
            ProjectionContext.FromEnvelope(CreateEnvelope(new OrderCreated(orderId, 42m))));

        var set = Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        Assert.Equal(42m, set.State.Amount);
    }

    [Fact]
    public void Apply_UndeclaredEvent_ShouldThrow()
    {
        var declaration = Declaration();
        var envelope = CreateEnvelope(new OrderNoteAdded(Guid.NewGuid(), "Test note"));

        // The processor filters on HandledEventTypes before dispatching, so reaching
        // Apply with an undeclared event is a wiring bug rather than a no-op.
        var ex = Assert.Throws<InvalidOperationException>(
            () => declaration.Apply(new OrderSummary(), envelope, ProjectionContext.FromEnvelope(envelope)));

        Assert.Contains("order-note-added", ex.Message);
    }

    #endregion

    #region GetDocumentId Tests

    [Fact]
    public void GetDocumentId_ShouldRouteByOrderId()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m));

        var docId = declaration.GetDocumentId(envelope);

        Assert.Equal(orderId.ToString(), docId);
    }

    [Fact]
    public void GetDocumentId_TypedOverload_ShouldRouteByOrderId()
    {
        var declaration = Declaration();
        var orderId = Guid.NewGuid();

        var docId = declaration.GetDocumentId(new OrderCreated(orderId, 100m));

        Assert.Equal(orderId.ToString(), docId);
    }

    #endregion

    #region Builder Contract

    [Fact]
    public void Build_WithoutHandlers_ShouldThrow()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeclareProjection.For<OrderSummary>("order-summary").Build());

        Assert.Contains(".On<TEvent>", ex.Message);
    }

    [Fact]
    public void On_SameEventTypeTwice_ShouldThrow()
    {
        var builder = DeclareProjection.For<OrderSummary>("order-summary")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state);

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state));

        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void Build_ShouldFreezeHandledTypesAndHandlersTogether()
    {
        var builder = DeclareProjection.For<OrderSummary>("order-summary")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state);
        var first = builder.Build();

        builder.On<OrderConfirmed>(
            id: e => e.OrderId.ToString(),
            apply: (state, e, ctx) => state);
        var second = builder.Build();

        Assert.DoesNotContain("order-confirmed", first.HandledEventTypes);
        Assert.Contains("order-confirmed", second.HandledEventTypes);
        Assert.False(first.HandledEventTypes is HashSet<string>);
    }

    [Fact]
    public void For_WithBlankProcessorId_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => DeclareProjection.For<OrderSummary>("  "));
    }

    [Fact]
    public void CollectionName_ShouldDefaultToProcessorId()
    {
        var declaration = Declaration();

        Assert.Equal("order-summary", declaration.ProcessorId);
        Assert.Equal("order-summary", declaration.CollectionName);
    }

    [Fact]
    public void Collection_ShouldOverrideTheStorageName()
    {
        var declaration = DeclareProjection.For<OrderSummary>("order-summary")
            .Collection("order_summaries")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state)
            .Build();

        Assert.Equal("order-summary", declaration.ProcessorId);
        Assert.Equal("order_summaries", declaration.CollectionName);
    }

    [Fact]
    public void InitialState_ShouldDefaultToNewInstance()
    {
        var declaration = Declaration();

        var initial = declaration.InitialState();

        Assert.Equal(Guid.Empty, initial.OrderId);
        Assert.Equal(0m, initial.Amount);
        Assert.Equal("", initial.Status);
    }

    [Fact]
    public void InitialState_ShouldHonourTheOverride()
    {
        var declaration = DeclareProjection.For<OrderSummary>("order-summary")
            .InitialState(() => new OrderSummary { Status = "Pending" })
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => state)
            .Build();

        Assert.Equal("Pending", declaration.InitialState().Status);
    }

    #endregion

    #region Implicit Conversion Tests

    [Fact]
    public void ImplicitConversion_StateToSet_ShouldWork()
    {
        var state = new OrderSummary { OrderId = Guid.NewGuid(), Status = "Test" };

        ProjectionResult<OrderSummary> result = state; // Implicit conversion

        Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        var set = (ProjectionResult<OrderSummary>.Set)result;
        Assert.Equal(state.OrderId, set.State.OrderId);
    }

    #endregion

    #region Helper Methods

    private static IEventEnvelope CreateEnvelope<TEvent>(TEvent @event) where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));

        return new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            GlobalPosition = 1,
            EventType = new EventType(eventTypeId),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
