using Alberto.ComponentTests.Steps;
using Alberto.Example.ComponentTests.Orders.Steps.Orders;
using Alberto.Example.ComponentTests.Orders.Steps.Subscriptions;
using Xunit;

namespace Alberto.Example.ComponentTests.Orders.Features;

/// <summary>
/// Tests for OrderProjectionSubscription - verifies that the read model is maintained correctly
/// </summary>
public sealed class OrderSubscriptionTests(ITestOutputHelper testOutputHelper)
    : OrdersSubscriptionFixture(testOutputHelper)
{
    [Fact]
    public async Task WhenOrderIsCreated_ThenItExistsInReadModel()
    {
        var orderId = Guid.NewGuid();

        await UseCase()
            .Act(
                new CreateOrder(100m, "customer-123"),
                new WaitForSubscriptions())
            .Assert(
                new HttpSuccessResponse(),
                new OrderExistsInReadModel(orderId, "Created"));
    }

    [Fact]
    public async Task WhenOrderIsPlaced_ThenReadModelIsUpdated()
    {
        var orderId = Guid.NewGuid();

        await UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123"),
                new WaitForSubscriptions())
            .Act(
                new PlaceOrderStep(orderId),
                new WaitForSubscriptions())
            .Assert(
                new HttpSuccessResponse(),
                new OrderExistsInReadModel(orderId, "Placed"));
    }

    [Fact]
    public async Task WhenOrderIsShipped_ThenReadModelIsUpdated()
    {
        var orderId = Guid.NewGuid();

        await UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123", orderId),
                new WaitForSubscriptions(),
                new PlaceOrderStep(orderId),
                new WaitForSubscriptions())
            .Act(
                new ShipOrderStep(orderId, "TRACK-123"),
                new WaitForSubscriptions())
            .Assert(
                new HttpSuccessResponse(),
                new OrderExistsInReadModel(orderId, "Shipped"));
    }

    [Fact]
    public async Task WhenOrderIsCancelled_ThenReadModelIsUpdated()
    {
        var orderId = Guid.NewGuid();

        await UseCase()
            .Arrange(
                new CreateOrder(100m, "customer-123", orderId),
                new WaitForSubscriptions())
            .Act(
                new CancelOrder(orderId, "Customer request"),
                new WaitForSubscriptions())
            .Assert(
                new HttpSuccessResponse(),
                new OrderExistsInReadModel(orderId, "Cancelled"));
    }

    [Fact]
    public async Task WhenMultipleOrdersAreCreated_ThenAllExistInReadModel()
    {
        var orderId1 = Guid.NewGuid();
        var orderId2 = Guid.NewGuid();
        var orderId3 = Guid.NewGuid();

        await UseCase()
            .Act(
                new CreateOrder(100m, "customer-1", orderId1),
                new CreateOrder(200m, "customer-2", orderId2),
                new CreateOrder(300m, "customer-3", orderId3),
                new WaitForSubscriptions())
            .Assert(
                new OrderExistsInReadModel(orderId1, "Created"),
                new OrderExistsInReadModel(orderId2, "Created"),
                new OrderExistsInReadModel(orderId3, "Created"));
    }
}