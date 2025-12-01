using Alberto.EventSourcing.Aggregates;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders;

/// <summary>
/// Shared state for the Order aggregate.
/// This state is used across multiple commands (PlaceOrder, ShipOrder, CancelOrder).
/// </summary>
public sealed record OrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
    public decimal Amount { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string? TrackingNumber { get; init; }
}

/// <summary>
/// Shared projector for the Order aggregate.
/// Handles all order-related events and projects them into OrderState.
/// This projector is shared across all order commands.
/// </summary>
/// <example>
/// Usage in command handlers:
/// <code>
/// public sealed class PlaceOrderHandler(OrderEventStore eventStore) : ICommandHandler&lt;PlaceOrderCommand&gt;
/// {
///     private static readonly OrderAggregateProjector Aggregate = new();
///
///     public async Task&lt;Result&gt; Handle(PlaceOrderCommand command, CancellationToken ct)
///     {
///         var decision = await eventStore.Decide(
///             Aggregate,  // Shared aggregate projector
///             command.OrderId.ToString(),
///             state => PlaceOrderDecision.Decide(state, command.OrderId),
///             ct);
///
///         return decision.IsError ? Result.Fail(decision.Problems) : Result.Success();
///     }
/// }
/// </code>
/// </example>
public sealed class OrderAggregateProjector : IAggregateProjector<OrderState>
{
    public OrderState Apply(OrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated e => state with { Exists = true, Status = OrderStatus.Created, Amount = e.Amount, CustomerId = e.CustomerId },
            OrderPlaced e => state with { Status = OrderStatus.Placed },
            OrderShipped e => state with { Status = OrderStatus.Shipped, TrackingNumber = e.TrackingNumber },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }

    public StreamQuery GetQuery(string aggregateId)
    {
        return new StreamQuery([new EventTag(Tags.Order, aggregateId)])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();
    }
}