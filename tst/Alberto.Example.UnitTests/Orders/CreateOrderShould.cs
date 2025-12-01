using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.Events;
using Alberto.UnitTests;
using Xunit;

namespace Alberto.Example.UnitTests.Orders;

public class CreateOrderShould
{
    [Fact]
    public void EmitOrderCreated()
    {
        var orderId = Guid.NewGuid();
        new Specification()
            .When(() => CreateOrderDecision.Decide(100m, "customer-123", orderId))
            .ThenEventOfType<OrderCreated>(e => e is { Amount: 100m, CustomerId: "customer-123", OrderId: var id } && id == orderId);
    }
}