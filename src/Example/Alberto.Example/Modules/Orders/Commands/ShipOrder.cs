using Alberto.CQRS.Commands;
using Alberto.CQRS.Results;
using Alberto.EventSourcing;
using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Alberto.EventStore.Events;
using Alberto.Example.Modules.Orders.Enums;
using Alberto.Example.Modules.Orders.Events;

namespace Alberto.Example.Modules.Orders.Commands;

public sealed record ShipOrderCommand(Guid OrderId, string TrackingNumber) : ICommand;

public sealed class ShipOrderHandler(OrderEventStore eventStore)
    : ICommandHandler<ShipOrderCommand>
{
    public async Task<Result> Handle(ShipOrderCommand command, CancellationToken cancellationToken = default)
    {
        var query = new StreamQuery([new EventTag(Tags.Order, command.OrderId.ToString())])
            .WithEventType<OrderCreated>()
            .WithEventType<OrderPlaced>()
            .WithEventType<OrderShipped>()
            .WithEventType<OrderCancelled>();

        var decision = await eventStore.Decide(
            new ShipOrderProjector(),
            query,
            state => ShipOrderDecision.Decide(state, command.OrderId, command.TrackingNumber),
            cancellationToken);

        return decision.IsError
            ? Result.Fail(decision.Problems)
            : Result.Success();
    }
}

internal sealed record ShipOrderState
{
    public bool Exists { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.Draft;
}

internal sealed class ShipOrderProjector : IProjector<ShipOrderState>
{
    public ShipOrderState Apply(ShipOrderState state, object @event)
    {
        return @event switch
        {
            OrderCreated => state with { Exists = true, Status = OrderStatus.Created },
            OrderPlaced => state with { Status = OrderStatus.Placed },
            OrderShipped => state with { Status = OrderStatus.Shipped },
            OrderCancelled => state with { Status = OrderStatus.Cancelled },
            _ => state
        };
    }
}

internal static class ShipOrderDecision
{
    public static Decision Decide(ShipOrderState state, Guid orderId, string trackingNumber)
    {
        if (!state.Exists)
            return Decision.Fail(OrderProblems.OrderNotFound(orderId));

        if (state.Status != OrderStatus.Placed)
            return Decision.Fail(OrderProblems.InvalidStatusForShipping(state.Status));

        var orderShipped = new OrderShipped(orderId, trackingNumber);
        return Decision.Succeed(orderShipped);
    }
}