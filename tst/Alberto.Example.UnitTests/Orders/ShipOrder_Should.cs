using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;
using Alberto.UnitTests;
using Xunit;

namespace Alberto.Example.UnitTests.Orders;

public class ShipOrder_Should
{
    [Fact]
    public void Given_OrderPlaced_EmitOrderShipped()
    {
        var orderId = Guid.NewGuid();
        var trackingNumber = "TRACK-12345";
        var decider = new ShipOrderDecider();

        new Specification<ShipOrderState>(decider)
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderPlaced(orderId, 100m, "customer-123"))
            .When(state => decider.Decide(state, orderId, trackingNumber))
            .ThenEventOfType<OrderShipped>(e => e.OrderId == orderId && e.TrackingNumber == trackingNumber);
    }

    [Fact]
    public void Given_NoEvents_FailWithOrderNotFound()
    {
        var orderId = Guid.NewGuid();
        var decider = new ShipOrderDecider();

        new Specification<ShipOrderState>(decider)
            .When(state => decider.Decide(state, orderId, "TRACK-123"))
            .ThenFailWith(OrderProblems.OrderNotFound(orderId));
    }

    [Fact]
    public void Given_OrderCreatedNotPlaced_FailWithInvalidOrderStatus()
    {
        var orderId = Guid.NewGuid();
        var decider = new ShipOrderDecider();

        new Specification<ShipOrderState>(decider)
            .Given(new OrderCreated(orderId, 100m, "customer-123"))
            .When(state => decider.Decide(state, orderId, "TRACK-123"))
            .ThenFailWith(OrderProblems.InvalidStatusForShipping(OrderStatus.Created));
    }

    [Fact]
    public void Given_OrderCancelled_FailWithInvalidOrderStatus()
    {
        var orderId = Guid.NewGuid();
        var decider = new ShipOrderDecider();

        new Specification<ShipOrderState>(decider)
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderCancelled(orderId, "Customer request"))
            .When(state => decider.Decide(state, orderId, "TRACK-123"))
            .ThenFailWith(OrderProblems.InvalidStatusForShipping(OrderStatus.Cancelled));
    }

    [Fact]
    public void Given_OrderAlreadyShipped_FailWithInvalidOrderStatus()
    {
        var orderId = Guid.NewGuid();
        var decider = new ShipOrderDecider();

        new Specification<ShipOrderState>(decider)
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderPlaced(orderId, 100m, "customer-123"),
                new OrderShipped(orderId, "TRACK-123"))
            .When(state => decider.Decide(state, orderId, "TRACK-456"))
            .ThenFailWith(OrderProblems.InvalidStatusForShipping(OrderStatus.Shipped));
    }
}