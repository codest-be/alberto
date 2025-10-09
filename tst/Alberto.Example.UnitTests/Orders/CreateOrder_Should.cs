using Alberto.Example.Modules.Orders.Commands;
using Alberto.Example.Modules.Orders.Events;
using Alberto.UnitTests;
using Xunit;

namespace Alberto.Example.UnitTests.Orders;

public class CreateOrder_Should
{
    [Fact]
    public void EmitOrderCreated()
    {
        new Specification()
            .When(() => new CreateOrderDecider().Decide(100m, "customer-123"))
            .ThenEventOfType<OrderCreated>(e => e is { Amount: 100m, CustomerId: "customer-123" });
    }
}