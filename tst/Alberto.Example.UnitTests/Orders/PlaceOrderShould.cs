using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;
using Alberto.UnitTests;
using Xunit;

namespace Alberto.Example.UnitTests.Orders;

public class PlaceOrderShould
{
    [Fact]
    public void Given_OrderCreated_EmitOrderPlaced()
    {
        var orderId = Guid.NewGuid();
        var projector = new PlaceOrderProjector();

        new Specification<PlaceOrderState>(projector)
            .Given(new OrderCreated(orderId, 100m, "customer-123"))
            .When(state => PlaceOrderDecision.Decide(state, orderId))
            .ThenEventOfType<OrderPlaced>(e =>
                e.OrderId == orderId && e.Amount == 100m && e.CustomerId == "customer-123");
    }

    [Fact]
    public void Given_NoEvents_FailWithOrderNotFound()
    {
        var orderId = Guid.NewGuid();
        var projector = new PlaceOrderProjector();

        new Specification<PlaceOrderState>(projector)
            .When(state => PlaceOrderDecision.Decide(state, orderId))
            .ThenFailWith(OrderProblems.OrderNotFound(orderId));
    }

    [Fact]
    public void Given_OrderAlreadyPlaced_FailWithInvalidOrderStatus()
    {
        var orderId = Guid.NewGuid();
        var projector = new PlaceOrderProjector();

        new Specification<PlaceOrderState>(projector)
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderPlaced(orderId, 100m, "customer-123"))
            .When(state => PlaceOrderDecision.Decide(state, orderId))
            .ThenFailWith(OrderProblems.InvalidStatusForPlacing(OrderStatus.Placed));
    }

    [Fact]
    public void Given_OrderCancelled_FailWithInvalidOrderStatus()
    {
        var orderId = Guid.NewGuid();
        var projector = new PlaceOrderProjector();

        new Specification<PlaceOrderState>(projector)
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderCancelled(orderId, "Customer request"))
            .When(state => PlaceOrderDecision.Decide(state, orderId))
            .ThenFailWith(OrderProblems.InvalidStatusForPlacing(OrderStatus.Cancelled));
    }
}