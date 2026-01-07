using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Tests for the Projection<TState> base class.
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

    public class OrderSummaryProjection : Projection<OrderSummary>,
        IProject<OrderSummary, OrderCreated>,
        IProject<OrderSummary, OrderConfirmed>,
        IProject<OrderSummary, OrderCancelled>
    {
        public string GetDocumentId(OrderCreated @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCreated @event, ProjectionContext context)
            => new OrderSummary { OrderId = @event.OrderId, Amount = @event.Amount, Status = "Created" };

        public string GetDocumentId(OrderConfirmed @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderConfirmed @event, ProjectionContext context)
            => state with { Status = "Confirmed" };

        public string GetDocumentId(OrderCancelled @event) => @event.OrderId.ToString();
        public ProjectionResult<OrderSummary> Apply(OrderSummary state, OrderCancelled @event, ProjectionContext context)
            => ProjectionResults.Delete<OrderSummary>();
    }

    #endregion

    #region Handled Event Types

    [Fact]
    public void HandledEventTypes_ShouldContainImplementedTypes()
    {
        var projection = new OrderSummaryProjection();

        Assert.Contains("order-created", projection.HandledEventTypes);
        Assert.Contains("order-confirmed", projection.HandledEventTypes);
        Assert.Contains("order-cancelled", projection.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ShouldNotContainUnimplementedTypes()
    {
        var projection = new OrderSummaryProjection();

        Assert.DoesNotContain("order-note-added", projection.HandledEventTypes);
    }

    #endregion

    #region Apply Tests

    [Fact]
    public void Apply_OrderCreated_ShouldReturnSetWithNewState()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m));

        var result = projection.Apply(new OrderSummary(), envelope);

        Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        var set = (ProjectionResult<OrderSummary>.Set)result;
        Assert.Equal(orderId, set.State.OrderId);
        Assert.Equal(100m, set.State.Amount);
        Assert.Equal("Created", set.State.Status);
    }

    [Fact]
    public void Apply_OrderConfirmed_ShouldReturnSetWithUpdatedStatus()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();
        var existingState = new OrderSummary { OrderId = orderId, Amount = 100m, Status = "Created" };
        var envelope = CreateEnvelope(new OrderConfirmed(orderId));

        var result = projection.Apply(existingState, envelope);

        Assert.IsType<ProjectionResult<OrderSummary>.Set>(result);
        var set = (ProjectionResult<OrderSummary>.Set)result;
        Assert.Equal("Confirmed", set.State.Status);
        Assert.Equal(100m, set.State.Amount); // Amount preserved
    }

    [Fact]
    public void Apply_OrderCancelled_ShouldReturnDelete()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();
        var existingState = new OrderSummary { OrderId = orderId, Amount = 100m, Status = "Created" };
        var envelope = CreateEnvelope(new OrderCancelled(orderId));

        var result = projection.Apply(existingState, envelope);

        Assert.IsType<ProjectionResult<OrderSummary>.Delete>(result);
    }

    [Fact]
    public void Apply_UnhandledEvent_ShouldReturnUnchanged()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderNoteAdded(orderId, "Test note"));

        var result = projection.Apply(new OrderSummary(), envelope);

        Assert.IsType<ProjectionResult<OrderSummary>.Unchanged>(result);
    }

    #endregion

    #region GetDocumentId Tests

    [Fact]
    public void GetDocumentId_ShouldRouteByOrderId()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();
        var envelope = CreateEnvelope(new OrderCreated(orderId, 100m));

        var docId = projection.GetDocumentId(envelope);

        Assert.Equal(orderId.ToString(), docId);
    }

    #endregion

    #region Fold Tests

    [Fact]
    public void Fold_ShouldBuildStateFromHistory()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();

        var events = new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m)),
            CreateEnvelope(new OrderConfirmed(orderId))
        };

        var result = projection.Fold(new OrderSummary(), events);

        Assert.Equal(orderId, result.OrderId);
        Assert.Equal(100m, result.Amount);
        Assert.Equal("Confirmed", result.Status);
    }

    [Fact]
    public void Fold_WithDelete_ShouldResetState()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();

        var events = new[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m)),
            CreateEnvelope(new OrderCancelled(orderId))
        };

        var result = projection.Fold(new OrderSummary(), events);

        // After delete, state is reset to new()
        Assert.Equal(Guid.Empty, result.OrderId);
        Assert.Equal(0m, result.Amount);
        Assert.Equal("", result.Status);
    }

    [Fact]
    public void Fold_WithUnhandledEvents_ShouldIgnoreThem()
    {
        var projection = new OrderSummaryProjection();
        var orderId = Guid.NewGuid();

        var events = new IEventEnvelope[]
        {
            CreateEnvelope(new OrderCreated(orderId, 100m)),
            CreateEnvelope(new OrderNoteAdded(orderId, "Test note")), // Unhandled
            CreateEnvelope(new OrderConfirmed(orderId))
        };

        var result = projection.Fold(new OrderSummary(), events);

        Assert.Equal("Confirmed", result.Status);
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
