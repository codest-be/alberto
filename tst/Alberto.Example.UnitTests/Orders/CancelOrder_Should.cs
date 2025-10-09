using Alberto.Example.Modules.Orders;
using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.Events;
using Alberto.UnitTests;
using Xunit;

namespace Alberto.Example.UnitTests.Orders;

public class CancelOrder_Should
{
    [Fact]
    public void Given_OrderCreated_EmitOrderCancelled()
    {
        var orderId = Guid.NewGuid();

        new Specification<CancelOrderState>(new CancelOrderDecider())
            .Given(new OrderCreated(orderId, 100m, "customer-123"))
            .When(state => CancelOrderDecider.Decide(state, orderId, "Customer request"))
            .ThenEventOfType<OrderCancelled>(e => e.OrderId == orderId && e.Reason == "Customer request");
    }

    [Fact]
    public void Given_OrderPlaced_EmitOrderCancelled()
    {
        var orderId = Guid.NewGuid();

        new Specification<CancelOrderState>(new CancelOrderDecider())
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderPlaced(orderId, 100m, "customer-123"))
            .When(state => CancelOrderDecider.Decide(state, orderId, "Customer request"))
            .ThenEventOfType<OrderCancelled>(e => e.OrderId == orderId && e.Reason == "Customer request");
    }

    [Fact]
    public void Given_NoEvents_FailWithOrderNotFound()
    {
        var orderId = Guid.NewGuid();

        new Specification<CancelOrderState>(new CancelOrderDecider())
            .When(state => CancelOrderDecider.Decide(state, orderId, "Customer request"))
            .ThenFailWith(OrderProblems.OrderNotFound(orderId));
    }

    [Fact]
    public void Given_OrderAlreadyCancelled_FailWithOrderAlreadyCancelled()
    {
        var orderId = Guid.NewGuid();

        new Specification<CancelOrderState>(new CancelOrderDecider())
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderCancelled(orderId, "Previous cancellation"))
            .When(state => CancelOrderDecider.Decide(state, orderId, "Customer request"))
            .ThenFailWith(OrderProblems.OrderAlreadyCancelled());
    }

    [Fact]
    public void Given_OrderShipped_FailWithCannotCancelShippedOrder()
    {
        var orderId = Guid.NewGuid();

        new Specification<CancelOrderState>(new CancelOrderDecider())
            .Given(
                new OrderCreated(orderId, 100m, "customer-123"),
                new OrderPlaced(orderId, 100m, "customer-123"),
                new OrderShipped(orderId, "TRACK-123"))
            .When(state => CancelOrderDecider.Decide(state, orderId, "Customer request"))
            .ThenFailWith(OrderProblems.CannotCancelShippedOrder());
    }
}