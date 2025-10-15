using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Alberto.Example.Modules.Orders.Events;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

public class OrderStatisticsTests(ITestOutputHelper testOutputHelper) : OrdersFixture(testOutputHelper)
{
    [Fact]
    public async Task Should_Track_Order_Statistics_Across_Multiple_Orders()
    {
        await UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-1"),
                new PlaceOrder(),
                new ShipOrder("TRACK-001"),
                new CreateOrder(200m, "customer-2"),
                new PlaceOrder(),
                new CancelOrder("Out of stock"),
                new CreateOrder(300m, "customer-3"),
                new PlaceOrder()
            )
            .Assert(
                new EventIsConsumed<OrderPlaced>().WithPredicate((ctx, e) => e.OrderId == ctx.GetOrderId()),
                new GetOrderStatistics(
                    totalOrders: 3,
                    totalAmount: 600m,
                    createdCount: 3,
                    placedCount: 3,
                    shippedCount: 1,
                    cancelledCount: 1
                )
            );
    }
}