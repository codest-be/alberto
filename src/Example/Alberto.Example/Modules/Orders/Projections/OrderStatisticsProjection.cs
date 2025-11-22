using Alberto.EventSourcing.Projections;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.Example.Modules.Orders.Events;
using Alberto.Projections;

namespace Alberto.Example.Modules.Orders.Projections;

[Subscription("order-statistics")]
public class OrderStatisticsSubscription(
    IProjectionRepository<string, OrderStatistics> repository,
    OrderStatisticsProjector projector) :
    IProjectionSubscription<string, OrderStatistics>,
    IHandleEvent<OrderCreated>,
    IHandleEvent<OrderPlaced>,
    IHandleEvent<OrderShipped>,
    IHandleEvent<OrderCancelled>
{
    // All events use the same key "global" for statistics
    private const string GlobalKey = "global";

    // Handle methods can be empty - EventRouter routes via IProjectionSubscription
    public ValueTask Handle(OrderCancelled @event, EventContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask Handle(OrderCreated @event, EventContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask Handle(OrderPlaced @event, EventContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask Handle(OrderShipped @event, EventContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public IProjector<OrderStatistics> Projector => projector;
    public IProjectionRepository<string, OrderStatistics> Repository => repository;

    public string GetKey(object @event) => GlobalKey;
}

public record OrderStatistics
{
    public int TotalOrders { get; init; }
    public decimal TotalAmount { get; init; }
    public int CreatedCount { get; init; }
    public int PlacedCount { get; init; }
    public int ShippedCount { get; init; }
    public int CancelledCount { get; init; }
}

[GenerateMigration(Schema = "orders", TableName = "order_statistics")]
public class OrderStatisticsProjector : IProjector<OrderStatistics>
{
    public OrderStatistics Apply(OrderStatistics projectionState, object @event)
    {
        return @event switch
        {
            OrderCreated e => projectionState with
            {
                TotalOrders = projectionState.TotalOrders + 1,
                TotalAmount = projectionState.TotalAmount + e.Amount,
                CreatedCount = projectionState.CreatedCount + 1
            },
            OrderPlaced => projectionState with { PlacedCount = projectionState.PlacedCount + 1 },
            OrderShipped => projectionState with { ShippedCount = projectionState.ShippedCount + 1 },
            OrderCancelled => projectionState with { CancelledCount = projectionState.CancelledCount + 1 },
            _ => projectionState
        };
    }
}